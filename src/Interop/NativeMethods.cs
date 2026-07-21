using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace SETTMemoryCleaner
{
    /// <summary>
    /// Win32 API Interop - DllImport declarações.
    ///
    /// PROPÓSITO:
    /// - Declarar funções Windows API (kernel32.dll, ntdll.dll, user32.dll, etc)
    /// - Usado por ComputerService (otimização RAM), HotKeyService, App.xaml.cs
    ///
    /// DLLS USADAS:
    /// - psapi.dll: EmptyWorkingSet (libera RAM processo)
    /// - ntdll.dll: NtSetSystemInformation (limpa cache sistema)
    /// - kernel32.dll: GlobalMemoryStatusEx (stats RAM), CreateFile, FlushFileBuffers
    /// - user32.dll: RegisterHotKey, FindWindow
    /// - advapi32.dll: AdjustTokenPrivileges (elevar privilégios admin)
    /// - dwmapi.dll: DwmSetWindowAttribute (tema janela)
    /// </summary>
    internal static class NativeMethods
    {
        // ADVAPI32.DLL - Privilégios Admin:

        /// <summary>
        /// Ajusta privilégios token processo (elevar para admin).
        /// USADO: ComputerService.SetIncreasePrivilege() - SeDebugName, SeProfSingleProcessName
        /// REQUER: App executando como admin
        /// </summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref Structs.WinAPI.TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

        /// <summary>
        /// Permite processo trazer janela para frente (foreground).
        /// USADO: Minimizar para tray → Restaurar janela
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int dwProcessId);

        /// <summary>
        /// Anexa console ao processo parent (modo CLI debug).
        /// USADO: Modo desenvolvedor (output console)
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(int dwProcessId);

        // KERNEL32.DLL - File I/O:

        /// <summary>
        /// Abre handle arquivo/volume (usado para flush cache disco).
        /// USADO: ComputerService.OptimizeModifiedFileCache() - flush cache drives
        /// </summary>
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern SafeFileHandle CreateFile([MarshalAs(UnmanagedType.LPWStr)] string lpFileName, FileAccess dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, int dwFlagsAndAttributes, IntPtr hTemplateFile);

        /// <summary>
        /// Destroi ícone (libera recurso).
        /// USADO: NotificationService (tray icon customizado)
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Envia comando I/O controle para device (disco).
        /// USADO: ComputerService.OptimizeModifiedFileCache() - reset write order, discard volume cache
        /// </summary>
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle hDevice, int dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        /// <summary>
        /// Define atributo DWM janela (bordas, cantos arredondados).
        /// USADO: MainWindow (tema dark, cantos arredondados Windows 11)
        /// </summary>
        [DllImport("dwmapi.dll", SetLastError = true)]
        internal static extern void DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        // PSAPI.DLL - Process Memory:

        /// <summary>
        /// PRINCIPAL: Libera Working Set processo (RAM → paging file).
        /// USADO: ComputerService.OptimizeWorkingSet() - CORAÇÃO otimização RAM
        /// EFEITO: Processo continua rodando, mas RAM liberada para pool disponível
        /// </summary>
        [SuppressUnmanagedCodeSecurity]
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr hProcess);

        // USER32.DLL - Window Management:

        /// <summary>
        /// Encontra janela por class/title.
        /// USADO: Single-instance check (detectar app já rodando)
        /// </summary>
        [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        /// <summary>
        /// Flush buffers arquivo para disco.
        /// USADO: ComputerService.OptimizeModifiedFileCache() - garantir write disco
        /// </summary>
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushFileBuffers(SafeFileHandle hFile);

        /// <summary>
        /// Obtém handle stdin/stdout/stderr.
        /// USADO: Modo console debug
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int nStdHandle);

        /// <summary>
        /// Obtém process ID da janela.
        /// USADO: Identificar processo dono janela
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        // KERNEL32.DLL - Memory Status:

        /// <summary>
        /// PRINCIPAL: Obtém status memória (RAM total, usada, livre).
        /// USADO: ComputerService.Memory - atualizado a cada 1 segundo
        /// RETORNA: Struct MemoryStatusEx (Total, Used, Free, % usado)
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx([In, Out] Structs.WinAPI.MemoryStatusEx lpBuffer);

        /// <summary>
        /// Verifica se janela está visível.
        /// USADO: Detectar janela minimizada/oculta
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// Obtém LUID (Locally Unique Identifier) de privilégio.
        /// USADO: ComputerService.SetIncreasePrivilege() - converter nome privilégio → LUID
        /// EX: "SeDebugPrivilege" → 20 (LUID)
        /// </summary>
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, ref long lpLuid);

        // NTDLL.DLL - System Information:

        /// <summary>
        /// PRINCIPAL: Define informação sistema (limpa cache RAM sistema).
        /// USADO: ComputerService - Otimiza StandbyList, CombinedPageList, ModifiedPageList, etc
        /// COMMANDS:
        /// - MemoryPurgeStandbyList: Limpa lista standby (cache passivo)
        /// - MemoryEmptyWorkingSets: Limpa Working Set TODOS processos
        /// - MemoryFlushModifiedList: Flush páginas modificadas para disco
        /// REQUER: Privilégios admin elevados
        /// </summary>
        [SuppressUnmanagedCodeSecurity]
        [DllImport("ntdll.dll", SetLastError = true)]
        internal static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength);

        // USER32.DLL - Hotkeys:

        /// <summary>
        /// Registra hotkey global (ex: Ctrl+Alt+O).
        /// USADO: HotKeyService - atalhos teclado otimização
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetSystemFileCacheSize(IntPtr minimumFileCacheSize, IntPtr maximumFileCacheSize, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
