using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace SETTMemoryCleaner
{
    /// <summary>
    /// Serviço Otimização RAM.
    ///
    /// RESPONSABILIDADES:
    /// - Obter stats RAM atual (via Win32 GlobalMemoryStatusEx)
    /// - Otimizar RAM chamando Win32 API (EmptyWorkingSet, NtSetSystemInformation)
    /// - Detectar OS Windows (XP, Vista, 7, 8, 10, 11)
    /// - Listar processos rodando
    ///
    /// USADO POR: MainViewModel
    /// CHAMA: NativeMethods (Win32 Interop)
    /// </summary>
    public class ComputerService : IComputerService
    {
        #region Fields

        // Cache stats RAM (atualizada a cada chamada Memory.get)
        private Memory _memory = new Memory(new Structs.WinAPI.MemoryStatusEx());

        // Cache info OS (detectada 1x só)
        private OperatingSystem _operatingSystem;

        #endregion

        #region Helpers

        /// <summary>
        /// Libera GCHandle com segurança.
        ///
        /// CONTEXTO: Otimização RAM usa GCHandle.Alloc() para passar structs para Win32 API.
        /// Após uso, DEVE liberar handle para evitar memory leak.
        ///
        /// THREAD-SAFE: Trata InvalidOperationException (handle já liberado por outra thread)
        /// </summary>
        private static void SafeFreeHandle(GCHandle handle)
        {
            try
            {
                if (handle.IsAllocated)
                    handle.Free();  // Libera handle nativo
            }
            catch (InvalidOperationException)
            {
                // Handle já liberado? Ignorar (race condition normal em multithreading)
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the memory info (RAM)
        /// </summary>
        public Memory Memory
        {
            get
            {
                try
                {
                    var memoryStatusEx = new Structs.WinAPI.MemoryStatusEx();

                    if (!NativeMethods.GlobalMemoryStatusEx(memoryStatusEx))
                        Logger.Error(new Win32Exception(Marshal.GetLastWin32Error()));

                    _memory = new Memory(memoryStatusEx);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }

                return _memory;
            }
        }

        /// <summary>
        /// Gets the operating system info
        /// </summary>
        public OperatingSystem OperatingSystem
        {
            get
            {
                if (_operatingSystem == null)
                {
                    var operatingSystem = Environment.OSVersion;

                    _operatingSystem = new OperatingSystem
                    {
                        Is64Bit = Environment.Is64BitOperatingSystem,
                        IsWindows7OrGreater = (operatingSystem.Version.Major > 6) || (operatingSystem.Version.Major == 6 && operatingSystem.Version.Minor >= 1),
                        IsWindows8OrGreater = operatingSystem.Version.Major >= 6.2,
                        IsWindows81OrGreater = operatingSystem.Version.Major >= 6.3,
                        IsWindowsVistaOrGreater = operatingSystem.Version.Major >= 6,
                        IsWindowsXpOrGreater = operatingSystem.Version.Major >= 5.1
                    };
                }

                return _operatingSystem;
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Occurs when [optimize progress is update].
        /// </summary>
        public event Action<byte, string> OnOptimizeProgressUpdate;

        #endregion

        #region Methods (Computer)

        private static SafeFileHandle OpenVolumeHandle(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                return null;

            return NativeMethods.CreateFile
            (
                @"\\.\" + driveLetter.TrimEnd(':', '\\') + ":",
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                FileMode.Open,
                (int)FileAttributes.Normal | Constants.WinAPI.File.FlagsNoBuffering,
                IntPtr.Zero
            );
        }

        /// <summary>
        /// Increase the Privilege using a privilege name
        /// </summary>
        /// <param name="privilegeName">The name of the privilege that needs to be increased</param>
        /// <returns></returns>
        private bool SetIncreasePrivilege(string privilegeName)
        {
            var result = false;

            using (var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges))
            {
                Structs.WinAPI.TokenPrivileges newState;
                newState.Count = 1;
                newState.Luid = 0L;
                newState.Attr = Constants.WinAPI.PrivilegeAttribute.Enabled;

                // Retrieves the uid used on a specified system to locally represent the specified privilege name
                if (NativeMethods.LookupPrivilegeValue(null, privilegeName, ref newState.Luid))
                {
                    // Enables or disables privileges in a specified access token
                    result = NativeMethods.AdjustTokenPrivileges(current.Token, false, ref newState, 0, IntPtr.Zero, IntPtr.Zero);
                }

                if (!result)
                    Logger.Error(new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return result;
        }

        #endregion

        #region Methods (Memory)

        /// <summary>
        /// MÉTODO PRINCIPAL: Otimiza RAM limpando áreas selecionadas.
        ///
        /// CHAMADO POR: MainViewModel.OptimizeAsync()
        ///
        /// FUNCIONAMENTO:
        /// 1. Para cada área selecionada (flags enum):
        ///    - Dispara evento OnOptimizeProgressUpdate (atualiza ProgressBar)
        ///    - Chama método específico (OptimizeWorkingSet, OptimizeStandbyList, etc)
        ///    - Método chama Win32 API (NtSetSystemInformation, EmptyWorkingSet)
        ///    - Loga duração e erros
        /// 2. Ao final: Loga resultado completo (total RAM liberada, tempo total)
        ///
        /// ÁREAS OTIMIZÁVEIS (Enums.Memory.Areas):
        /// - WorkingSet: Libera RAM processos (EmptyWorkingSet para cada processo)
        /// - SystemFileCache: Flush cache arquivos (NtSetSystemInformation)
        /// - ModifiedPageList: Flush páginas modificadas (NtSetSystemInformation)
        /// - StandbyList: Purga lista standby (NtSetSystemInformation)
        /// - CombinedPageList: Combina páginas físicas (NtSetSystemInformation)
        /// - ... mais 3 áreas
        ///
        /// REQUER: Privilégios admin (SeDebugName, SeProfSingleProcessName)
        /// </summary>
        /// <param name="reason">Motivo: Manual, Schedule, LowMemory</param>
        /// <param name="areas">Flags enum áreas para limpar</param>
        public void Optimize(Enums.Memory.Optimization.Reason reason, Enums.Memory.Areas areas)
        {
            // Validação: Nenhuma área selecionada? Sair
            if (areas == Enums.Memory.Areas.None)
                return;

            var errorRuntime = new TimeSpan();
            var infoRuntime = new TimeSpan();
            var optimizationReason = reason.GetString();
            var stopwatch = new Stopwatch();
            var value = (byte)0;

            var error = new LogOptimizationData { Reason = optimizationReason };
            var info = new LogOptimizationData { Reason = optimizationReason };

            // Optimize Working Set
            if ((areas & Enums.Memory.Areas.WorkingSet) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.Strings.WorkingSet);
                    }

                    stopwatch.Restart();

                    OptimizeWorkingSet();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.WorkingSet,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.WorkingSet,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize System File Cache
            if ((areas & Enums.Memory.Areas.SystemFileCache) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.Strings.SystemFileCache);
                    }

                    stopwatch.Restart();

                    OptimizeSystemFileCache();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.SystemFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.SystemFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Modified Page List
            if ((areas & Enums.Memory.Areas.ModifiedPageList) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.Strings.ModifiedPageList);
                    }

                    stopwatch.Restart();

                    OptimizeModifiedPageList();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.ModifiedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.ModifiedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Standby List
            if ((areas & (Enums.Memory.Areas.StandbyList | Enums.Memory.Areas.StandbyListLowPriority)) != 0)
            {
                var lowPriority = (areas & Enums.Memory.Areas.StandbyListLowPriority) != 0;
                var standbyList = lowPriority ? Localizer.Strings.StandbyListLowPriority : Localizer.Strings.StandbyList;

                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, standbyList);
                    }

                    stopwatch.Restart();

                    OptimizeStandbyList(lowPriority);

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = standbyList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = standbyList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Combined Page List
            if ((areas & Enums.Memory.Areas.CombinedPageList) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.Strings.CombinedPageList);
                    }

                    stopwatch.Restart();

                    OptimizeCombinedPageList();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.CombinedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.CombinedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Registry Cache
            if ((areas & Enums.Memory.Areas.RegistryCache) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.Strings.RegistryCache);
                    }

                    stopwatch.Restart();

                    OptimizeRegistryCache();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.RegistryCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.RegistryCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Modified File Cache
            if ((areas & Enums.Memory.Areas.ModifiedFileCache) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.Strings.ModifiedFileCache);
                    }

                    stopwatch.Restart();

                    OptimizeModifiedFileCache();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.ModifiedFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.Strings.ModifiedFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Garbage Collector
            try
            {
                if (OnOptimizeProgressUpdate != null)
                {
                    value++;
                    OnOptimizeProgressUpdate(value, Localizer.Strings.GarbageCollector);
                }

                App.ReleaseMemory();
            }
            catch
            {
                // ignored
            }

            // Log
            try
            {
                // Info
                if (info.MemoryAreas.Any())
                {
                    info.Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", infoRuntime.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture));

                    Logger.Log(new Log(Enums.Log.Levels.Information, Localizer.Strings.MemoryOptimized, info));
                }
                // Error
                if (error.MemoryAreas.Any())
                {
                    error.Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", errorRuntime.TotalSeconds, Localizer.Strings.Seconds.ToLower(Localizer.Culture));

                    Logger.Log(new Log(Enums.Log.Levels.Error, Localizer.Strings.Invalid, error));
                }
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Optimize the combined page list.
        /// </summary>
        private void OptimizeCombinedPageList()
        {
            // Windows minimum version
            if (!OperatingSystem.HasCombinedPageList)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.CombinedPageList));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.WinAPI.Privilege.SeProfSingleProcessName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorAdminPrivilegeRequired, Constants.WinAPI.Privilege.SeProfSingleProcessName));

            var handle = GCHandle.Alloc(0);

            try
            {
                var memoryCombineInformationEx = new Structs.WinAPI.MemoryCombineInformationEx();

                handle = GCHandle.Alloc(memoryCombineInformationEx, GCHandleType.Pinned);

                if (NativeMethods.NtSetSystemInformation(Constants.WinAPI.SystemInformationClass.SystemCombinePhysicalMemoryInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(memoryCombineInformationEx)) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                SafeFreeHandle(handle);
            }
        }

        /// <summary>
        /// Optimize the modified file cache.
        /// </summary>
        private void OptimizeModifiedFileCache()
        {
            // Windows minimum version
            if (!OperatingSystem.HasModifiedFileCache)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.ModifiedFileCache));

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive == null || drive.DriveType != DriveType.Fixed || string.IsNullOrWhiteSpace(drive.Name))
                    continue;

                using (var handle = OpenVolumeHandle(drive.Name))
                {
                    if (handle == null || handle.IsInvalid)
                        continue;

                    int bytesReturned;

                    if (OperatingSystem.IsWindows7OrGreater)
                    {
                        try
                        {
                            var buffer = Marshal.AllocHGlobal(1);

                            try
                            {
                                if (!NativeMethods.DeviceIoControl(
                                    handle,
                                    Constants.WinAPI.Drive.IoControlResetWriteOrder,
                                    buffer,
                                    1,
                                    IntPtr.Zero,
                                    0,
                                    out bytesReturned,
                                    IntPtr.Zero))
                                {
                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(buffer);
                            }
                        }
                        catch
                        {
                            // ignored
                        }

                        if (OperatingSystem.IsWindows8OrGreater)
                        {
                            try
                            {
                                if (!NativeMethods.DeviceIoControl(
                                    handle,
                                    Constants.WinAPI.Drive.FsctlDiscardVolumeCache,
                                    IntPtr.Zero,
                                    0,
                                    IntPtr.Zero,
                                    0,
                                    out bytesReturned,
                                    IntPtr.Zero))
                                {
                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                                }
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                    }

                    if (!NativeMethods.FlushFileBuffers(handle))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        /// <summary>
        /// Optimize the modified page list.
        /// </summary>
        private void OptimizeModifiedPageList()
        {
            // Windows minimum version
            if (!OperatingSystem.HasModifiedPageList)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.ModifiedPageList));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.WinAPI.Privilege.SeProfSingleProcessName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorAdminPrivilegeRequired, Constants.WinAPI.Privilege.SeProfSingleProcessName));

            var handle = GCHandle.Alloc(Constants.WinAPI.SystemMemoryListCommand.MemoryFlushModifiedList, GCHandleType.Pinned);

            try
            {
                if (NativeMethods.NtSetSystemInformation(Constants.WinAPI.SystemInformationClass.SystemMemoryListInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(Constants.WinAPI.SystemMemoryListCommand.MemoryFlushModifiedList)) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                SafeFreeHandle(handle);
            }
        }

        /// <summary>
        /// Optimize the registry cache.
        /// </summary>
        private void OptimizeRegistryCache()
        {
            // Windows minimum version
            if (!OperatingSystem.HasRegistryHive)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.RegistryCache));

            if (NativeMethods.NtSetSystemInformation(Constants.WinAPI.SystemInformationClass.SystemRegistryReconciliationInformation, IntPtr.Zero, 0) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// Optimize the standby list.
        /// </summary>
        /// <param name="lowPriority">if set to <c>true</c> [low priority].</param>
        private void OptimizeStandbyList(bool lowPriority = false)
        {
            // Windows minimum version
            if (!OperatingSystem.HasStandbyList)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.StandbyList));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.WinAPI.Privilege.SeProfSingleProcessName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorAdminPrivilegeRequired, Constants.WinAPI.Privilege.SeProfSingleProcessName));

            object memoryPurgeStandbyList = lowPriority ? Constants.WinAPI.SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList : Constants.WinAPI.SystemMemoryListCommand.MemoryPurgeStandbyList;
            var handle = GCHandle.Alloc(memoryPurgeStandbyList, GCHandleType.Pinned);

            try
            {
                if (NativeMethods.NtSetSystemInformation(Constants.WinAPI.SystemInformationClass.SystemMemoryListInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(memoryPurgeStandbyList)) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                SafeFreeHandle(handle);
            }
        }

        /// <summary>
        /// Optimize the system file cache.
        /// </summary>
        private void OptimizeSystemFileCache()
        {
            // Windows minimum version
            if (!OperatingSystem.HasSystemFileCache)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.SystemFileCache));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.WinAPI.Privilege.SeIncreaseQuotaName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorAdminPrivilegeRequired, Constants.WinAPI.Privilege.SeIncreaseQuotaName));

            var handle = GCHandle.Alloc(0);

            try
            {
                object systemFileCacheInformation;

                if (OperatingSystem.Is64Bit)
                    systemFileCacheInformation = new Structs.WinAPI.SystemFileCacheInformation64 { MinimumWorkingSet = -1L, MaximumWorkingSet = -1L };
                else
                    systemFileCacheInformation = new Structs.WinAPI.SystemFileCacheInformation32 { MinimumWorkingSet = int.MaxValue, MaximumWorkingSet = int.MaxValue };

                handle = GCHandle.Alloc(systemFileCacheInformation, GCHandleType.Pinned);

                if (NativeMethods.NtSetSystemInformation(Constants.WinAPI.SystemInformationClass.SystemFileCacheInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(systemFileCacheInformation)) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                SafeFreeHandle(handle);
            }

            var fileCacheSize = IntPtr.Subtract(IntPtr.Zero, 1); // Flush

            if (!NativeMethods.SetSystemFileCacheSize(fileCacheSize, fileCacheSize, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// Otimiza Working Set (RAM usada por processos).
        ///
        /// O QUE É WORKING SET:
        /// - Memória física (RAM) alocada para cada processo
        /// - Contém código exe, DLLs, dados, heap, stack
        /// - Windows mantém em RAM mesmo se processo idle
        ///
        /// O QUE FAZ:
        /// - Libera Working Set de cada processo via Win32 EmptyWorkingSet()
        /// - RAM liberada volta para pool disponível
        /// - Processos continuam rodando (dados movidos para paging file)
        /// - Performance: Pode causar lag inicial ao reabrir app (reload da RAM)
        ///
        /// PROCESS EXCLUSION LIST:
        /// - Se lista NÃO vazia: Itera processos individualmente, pula excluídos
        /// - Se lista VAZIA: Usa API global (mais rápido, limpa TODOS processos)
        ///
        /// PRIVILÉGIOS:
        /// - SeDebugName: Acesso processos protegidos (se exclusion list)
        /// - SeProfSingleProcessName: Acesso API global (se sem exclusion list)
        /// </summary>
        private void OptimizeWorkingSet()
        {
            // Validação: Windows suporta Working Set? (todos desde XP)
            if (!OperatingSystem.HasWorkingSet)
                throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorMemoryAreaOptimizationNotSupported, Localizer.Strings.WorkingSet));

            var errors = new StringBuilder();

            // MODO 1: PROCESS EXCLUSION LIST ATIVA (limpa individual, pula excluídos)
            if (Settings.ProcessExclusionList.Any())
            {
                // Solicitar privilégio admin (acesso processos protegidos)
                if (!SetIncreasePrivilege(Constants.WinAPI.Privilege.SeDebugName))
                    throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorAdminPrivilegeRequired, Constants.WinAPI.Privilege.SeDebugName));

                // Obter TODOS processos Windows, filtrando excluídos
                var processes = Process.GetProcesses().Where(process => process != null && !Settings.ProcessExclusionList.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase));

                // Iterar cada processo, limpando Working Set
                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            // WIN32 API: EmptyWorkingSet(process.Handle)
                            // Libera RAM do processo, move dados para paging file
                            if (!NativeMethods.EmptyWorkingSet(process.Handle))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        catch (InvalidOperationException)
                        {
                            // Processo terminou enquanto iterava? Ignorar
                        }
                        catch (Win32Exception e)
                        {
                            // Erro "Access Denied"? Processo protegido, ignorar
                            // Outros erros? Logar
                            if (e.NativeErrorCode != Constants.WinAPI.SystemErrorCode.ErrorAccessDenied)
                                errors.Append(string.Format(Localizer.Culture, "{0}: {1} | ", process.ProcessName, e.GetMessage()));
                        }
                    }
                }

                // Limpar separador final " | "
                if (errors.Length > 3)
                    errors.Remove(errors.Length - 3, 3);
            }
            else
            {
                // MODO 2: SEM EXCLUSION LIST (API global, limpa TUDO - mais rápido)

                // Solicitar privilégio admin (API global)
                if (!SetIncreasePrivilege(Constants.WinAPI.Privilege.SeProfSingleProcessName))
                    throw new Exception(string.Format(Localizer.Culture, Localizer.Strings.ErrorAdminPrivilegeRequired, Constants.WinAPI.Privilege.SeDebugName));

                // Alocar handle GC para passar comando Win32 API
                var handle = GCHandle.Alloc(Constants.WinAPI.SystemMemoryListCommand.MemoryEmptyWorkingSets, GCHandleType.Pinned);

                try
                {
                    // WIN32 API: NtSetSystemInformation(SystemMemoryListInformation, MemoryEmptyWorkingSets)
                    // Limpa Working Set de TODOS processos de uma vez (exceto kernel)
                    if (NativeMethods.NtSetSystemInformation(Constants.WinAPI.SystemInformationClass.SystemMemoryListInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(Constants.WinAPI.SystemMemoryListCommand.MemoryEmptyWorkingSets)) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                    {
                        var e = new Win32Exception(Marshal.GetLastWin32Error());

                        if (e != null)
                        {
                            if (errors.Length > 0)
                                errors.Append(" | ");

                            errors.Append(e.GetMessage());
                        }
                    }
                }
                finally
                {
                    // SEMPRE liberar handle (evita memory leak)
                    SafeFreeHandle(handle);
                }
            }

            // Se houve erros, throw (será logado por Optimize())
            if (errors.Length > 0)
                throw new Exception(errors.ToString());
        }

        #endregion
    }
}
