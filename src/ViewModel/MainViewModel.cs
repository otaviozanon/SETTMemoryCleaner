using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;

namespace SETTMemoryCleaner
{
    /// <summary>
    /// ViewModel principal da aplicação.
    /// Gerencia lógica UI, comandos de otimização, auto-otimização e bindings com MainWindow.xaml.
    ///
    /// ARQUITETURA MVVM:
    /// - View (MainWindow.xaml) faz binding nas propriedades deste ViewModel
    /// - ViewModel chama Services (ComputerService, HotkeyService) para lógica negócio
    /// - Notifica View via RaisePropertyChanged() quando dados mudam
    /// </summary>
    public class MainViewModel : ViewModel, IDisposable
    {
        #region Fields

        // Token cancelamento para operações assíncronas (monitor RAM, auto-otimização)
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        // Model: Dados computador (RAM, CPU, OS) - atualizado a cada 1 segundo
        private Computer _computer;

        // SERVIÇOS (Injeção Dependência):
        private readonly IComputerService _computerService;   // Otimização RAM (Win32 API)
        private readonly IHotkeyService _hotKeyService;        // Atalhos teclado globais (Ctrl+Alt+O)

        // Estados UI
        private bool _isOptimizationKeyValid;      // Hotkey válida?
        private bool _isOptimizationRunning;       // Otimização em andamento? (desabilita botão)
        private bool _isReiniziliating;            // Re-inicializando settings?

        // Timestamps auto-otimização (evitar execuções duplicadas)
        private DateTimeOffset _lastAutoOptimizationByInterval = DateTimeOffset.Now;       // Última execução por timer
        private DateTimeOffset _lastAutoOptimizationByMemoryUsage = DateTimeOffset.Now;    // Última execução por threshold RAM

        // Lock thread-safety (múltiplas threads acessam Computer)
        private readonly object _lockObject = new object();

        // Progress bar otimização
        private byte _optimizationProgressPercentage;          // 0-100%
        private string _optimizationProgressStep = Localizer.Strings.Optimize;  // "Cleaning Combined Page List..."
        private byte _optimizationProgressTotal = byte.MaxValue;                // Total steps
        private byte _optimizationProgressValue = byte.MinValue;                // Step atual

        // Process Exclusion List
        private string _selectedProcess;           // Processo selecionado no ComboBox

        // Tray Icon
        private ObservableCollection<ObservableItem<bool>> _trayIconItems;

        // CACHE PERFORMANCE: Lista processos carregada 1x só (lazy-load)
        // Evita lag ComboBox ao abrir Settings (200+ processos)
        private ObservableCollection<string> _processesCache;

        #endregion

        #region Constructors

        /// <summary>
        /// Construtor principal - inicializa comandos, propriedades e inicia monitoramento RAM.
        ///
        /// CHAMADO POR: ViewModelLocator.cs ao criar instância única (Singleton)
        /// QUANDO: App startup (App.xaml.cs → OnStartup)
        /// </summary>
        /// <param name="computerService">Serviço otimização RAM (Win32 API calls)</param>
        /// <param name="hotKeyService">Serviço hotkeys globais (Ctrl+Alt+O)</param>
        /// <param name="notificationService">Serviço notificações toast Windows</param>
        public MainViewModel(IComputerService computerService, IHotkeyService hotKeyService, INotificationService notificationService)
            : base(notificationService)
        {
            // Injeção dependência - serviços usados pelo ViewModel
            _computerService = computerService;
            _hotKeyService = hotKeyService;

            // INICIALIZAR COMANDOS (Actions WPF):
            // Binding XAML: <Button Command="{Binding OptimizeCommand}" />

            // Adicionar processo à lista exclusão
            AddProcessToExclusionListCommand = new RelayCommand<string>(AddProcessToExclusionList, () => CanAddProcessToExclusionList);

            // COMANDO PRINCIPAL: Otimizar RAM (botão OPTIMIZE)
            OptimizeCommand = new RelayCommand(() => OptimizeAsync(Enums.Memory.Optimization.Reason.Manual), () => CanOptimize);

            // Remover processo da lista exclusão
            RemoveProcessFromExclusionListCommand = new RelayCommand<string>(RemoveProcessFromExclusionList);

            // Reset settings para padrão
            ResetSettingsToDefaultConfigurationCommand = new RelayCommand(ResetSettingsToDefaultConfiguration);

            // INICIALIZAR PROPRIEDADES UI:
            FontSize = Settings.FontSize;  // Tamanho fonte (binding XAML)

            // Memory Usage Thresholds: 1-99% (slider auto-otimização por RAM)
            MemoryUsageThresholds = Enumerable.Range(1, 99).Select(number => (byte)number).ToList();

            // Model Computer: Dados RAM/CPU/OS
            Computer = new Computer();

            // MODO DESIGN (Visual Studio Designer):
            if (App.IsInDesignMode)
            {
                // Mock services para preview XAML
                _computerService = new ComputerService();
                _hotKeyService = new HotkeyService();

                Settings.AutoUpdate = true;

                // Simular Windows 10
                Computer.OperatingSystem.IsWindows81OrGreater = true;
                Computer.OperatingSystem.IsWindows8OrGreater = true;
                Computer.OperatingSystem.IsWindowsVistaOrGreater = true;
                Computer.OperatingSystem.IsWindowsXpOrGreater = true;
                IsOptimizationKeyValid = true;
            }
            else
            {
                // MODO RUNTIME (App real):

                // Escutar eventos progresso otimização (atualiza ProgressBar)
                _computerService.OnOptimizeProgressUpdate += OnOptimizeProgressUpdate;

                // Detectar OS atual
                Computer.OperatingSystem = _computerService.OperatingSystem;

                // Ativar hotkey se habilitada
                UseHotkey = Settings.UseHotkey;

                // INICIAR MONITORAMENTO:
                // - Atualiza RAM/CPU a cada 1 segundo
                // - Verifica auto-otimização (timer + threshold RAM)
                MonitorAsync();
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets a value indicating whether [always on top].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [always on top]; otherwise, <c>false</c>.
        /// </value>
        public bool AlwaysOnTop
        {
            get { return Settings.AlwaysOnTop; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.AlwaysOnTop = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the automatic optimization interval.
        /// </summary>
        /// <value>
        /// The automatic optimization interval.
        /// </value>
        public int AutoOptimizationInterval
        {
            get { return Settings.AutoOptimizationInterval; }
            set
            {
                try
                {
                    IsBusy = true;

                    _lastAutoOptimizationByInterval = DateTimeOffset.Now;

                    Settings.AutoOptimizationInterval = value;
                    Settings.Save();

                    RaisePropertyChanged();
                    RaisePropertyChanged(() => AutoOptimizationMemoryIntervalDescription);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the automatic optimization memory usage.
        /// </summary>
        /// <value>
        /// The automatic optimization memory usage.
        /// </value>
        public int AutoOptimizationMemoryUsage
        {
            get { return Settings.AutoOptimizationMemoryUsage; }
            set
            {
                try
                {
                    IsBusy = true;

                    _lastAutoOptimizationByMemoryUsage = DateTimeOffset.Now;

                    Settings.AutoOptimizationMemoryUsage = value;
                    Settings.Save();

                    RaisePropertyChanged();
                    RaisePropertyChanged(() => AutoOptimizationMemoryUsageDescription);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets the automatic optimization memory interval description.
        /// </summary>
        /// <value>
        /// The automatic optimization memory interval description.
        /// </value>
        public string AutoOptimizationMemoryIntervalDescription
        {
            get { return string.Format(Localizer.Culture, Localizer.Strings.EveryHour, AutoOptimizationInterval); }
        }

        /// <summary>
        /// Gets the automatic optimization memory usage description.
        /// </summary>
        /// <value>
        /// The automatic optimization memory usage description.
        /// </value>
        public string AutoOptimizationMemoryUsageDescription
        {
            get { return string.Format(Localizer.Culture, Localizer.Strings.WhenFreePhysicalMemoryIsBelow, AutoOptimizationMemoryUsage); }
        }

        /// <summary>
        /// Gets the automatic optimization memory usage warning.
        /// </summary>
        /// <value>
        /// The automatic optimization memory usage warning.
        /// </value>
        public string AutoOptimizationMemoryUsageWarning
        {
            get { return string.Format(Localizer.Culture, Localizer.Strings.AutoOptimizationInterval, Constants.App.AutoOptimizationMemoryUsageInterval); }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [automatic update].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [automatic update]; otherwise, <c>false</c>.
        /// </value>
        public bool AutoUpdate
        {
            get { return Settings.AutoUpdate; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.AutoUpdate = Helper.IsAutoUpdateSupported && value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets the brushes.
        /// </summary>
        /// <value>
        /// The brushes.
        /// </value>
        public ObservableCollection<SolidColorBrush> Brushes
        {
            get
            {
                return new ObservableCollection<SolidColorBrush>(App.IsInDesignMode ? new List<SolidColorBrush> { System.Windows.Media.Brushes.White } : ThemeManager.Brushes);
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance can add process to exclusion list.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance can add process to exclusion list; otherwise, <c>false</c>.
        /// </value>
        public bool CanAddProcessToExclusionList
        {
            get { return SelectedProcess != null; }
        }

        /// <summary>
        /// Gets a value indicating whether this instance can optimize.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance can optimize; otherwise, <c>false</c>.
        /// </value>
        public bool CanOptimize
        {
            get { return MemoryAreas != Enums.Memory.Areas.None && !IsOptimizationRunning; }
        }

        /// <summary>
        /// Gets a value indicating whether this instance can run on startup.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance can run on startup; otherwise, <c>false</c>.
        /// </value>
        public bool CanRunOnStartup
        {
            get { return true; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [close after optimization].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [close after optimization]; otherwise, <c>false</c>.
        /// </value>
        public bool CloseAfterOptimization
        {
            get { return Settings.CloseAfterOptimization; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.CloseAfterOptimization = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [close to the notification area].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [close to the notification area]; otherwise, <c>false</c>.
        /// </value>
        public bool CloseToTheNotificationArea
        {
            get { return Settings.CloseToTheNotificationArea; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.CloseToTheNotificationArea = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [compact mode].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [compact mode]; otherwise, <c>false</c>.
        /// </value>
        public bool CompactMode
        {
            get { return Settings.CompactMode; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.CompactMode = value;
                    Settings.Save();

                    RaisePropertyChanged();
                    RaisePropertyChanged(() => Title);

                    App.ReleaseMemory();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [create start menu shortcut].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [create start menu shortcut]; otherwise, <c>false</c>.
        /// </value>
        public bool CreateStartMenuShortcut
        {
            get { return Settings.CreateStartMenuShortcut; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.CreateStartMenuShortcut = value;
                    Settings.Save();

                    Helper.StartMenuShortcut(value);

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Model Computer: Dados RAM, CPU e OS.
        ///
        /// BINDINGS XAML:
        /// - {Binding Computer.Memory.PercentUsed} → ProgressBar RAM
        /// - {Binding Computer.Memory.Used} → "8.2 GB"
        /// - {Binding Computer.Memory.Total} → "16 GB"
        ///
        /// ATUALIZAÇÃO: MonitorAsync() atualiza a cada 1 segundo via timer
        /// </summary>
        public Computer Computer
        {
            get { return _computer; }
            private set
            {
                _computer = value;
                RaisePropertyChanged();  // Notifica View → UI atualiza automaticamente
            }
        }

        /// <summary>
        /// Gets or sets the size of the font.
        /// </summary>
        /// <value>
        /// The size of the font.
        /// </value>
        public double FontSize
        {
            get { return Settings.FontSize; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.FontSize = value;
                    Settings.Save();

                if (WpfApplication.Current != null)
                {
                    WpfApplication.Current.Dispatcher.Invoke((Action)(() =>
                    {
                        WpfApplication.Current.Resources["FontSize"] = value;
                        }));
                    }

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether optimization key is valid.
        /// </summary>
        /// <value>
        ///   <c>true</c> if optimization key is valid; otherwise, <c>false</c>.
        /// </value>
        public bool IsOptimizationKeyValid
        {
            get { return _isOptimizationKeyValid; }
            set
            {
                if (_isReiniziliating)
                    return;

                _isOptimizationKeyValid = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether is optimization running.
        /// </summary>
        /// <value>
        ///   <c>true</c> if is optimization running; otherwise, <c>false</c>.
        /// </value>
        public bool IsOptimizationRunning
        {
            get { return _isOptimizationRunning; }
            set
            {
                _isOptimizationRunning = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets the keyboard keys.
        /// </summary>
        /// <value>
        /// The keyboard keys.
        /// </value>
        public List<Key> KeyboardKeys
        {
            get
            {
                return _hotKeyService.Keys;
            }
        }

        /// <summary>
        /// Gets the keyboard modifiers.
        /// </summary>
        /// <value>
        /// The keyboard modifiers.
        /// </value>
        public Dictionary<ModifierKeys, string> KeyboardModifiers
        {
            get
            {
                return _hotKeyService.Modifiers;
            }
        }

        /// <summary>
        /// Gets or sets the language.
        /// </summary>
        /// <value>
        /// The language.
        /// </value>
        public Language Language
        {
            get
            {
                return Localizer.Language;
            }
            set
            {
                try
                {
                    IsBusy = true;

                    if (Localizer.Language != null && Localizer.Language.Equals(value))
                        return;

                    Localizer.Language = value;

                    if (!App.IsInDesignMode)
                    {
                        Computer.Memory = _computerService.Memory;
                        RaisePropertyChanged(() => Computer);

                        _trayIconItems = null;

                        NotificationService.Initialize();
                        NotificationService.Update(Computer.Memory, IsOptimizationRunning);
                    }

                    RaisePropertyChanged(string.Empty);

                    if (OnLanguageChangeCompleted != null)
                    {
                        WpfApplication.Current.Dispatcher.Invoke((Action)delegate
                        {
                            OnLanguageChangeCompleted();
                        });
                    }
                }
                catch (Exception e)
                {
                    NotificationService.Notify(e.GetMessage());
                    Logger.Error(e);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets the memory area items.
        /// </summary>
        /// <value>
        /// The memory area items.
        /// </value>
        public ObservableCollection<ObservableItem<bool>> MemoryAreaItems
        {
            get
            {
                var items = new List<ObservableItem<bool>>();

                Action<string, Enums.Memory.Areas, bool> add = (name, area, isEnabled) =>
                {

                    items.Add(new ObservableItem<bool>
                    (
                        name,
                        () => (MemoryAreas & area) == area,
                        (value) => { MemoryAreas = area; },
                        isEnabled
                    ));
                };

                add(Localizer.Strings.CombinedPageList, Enums.Memory.Areas.CombinedPageList, Computer.OperatingSystem.HasCombinedPageList);
                add(Localizer.Strings.ModifiedFileCache, Enums.Memory.Areas.ModifiedFileCache, Computer.OperatingSystem.HasModifiedFileCache);
                add(Localizer.Strings.ModifiedPageList, Enums.Memory.Areas.ModifiedPageList, Computer.OperatingSystem.HasModifiedPageList);
                add(Localizer.Strings.RegistryCache, Enums.Memory.Areas.RegistryCache, Computer.OperatingSystem.HasRegistryHive);
                add(Localizer.Strings.StandbyList, Enums.Memory.Areas.StandbyList, Computer.OperatingSystem.HasStandbyList);
                add(Localizer.Strings.StandbyListLowPriority, Enums.Memory.Areas.StandbyListLowPriority, Computer.OperatingSystem.HasStandbyList);
                add(Localizer.Strings.SystemFileCache, Enums.Memory.Areas.SystemFileCache, Computer.OperatingSystem.HasSystemFileCache);
                add(Localizer.Strings.WorkingSet, Enums.Memory.Areas.WorkingSet, Computer.OperatingSystem.HasWorkingSet);

                return new ObservableCollection<ObservableItem<bool>>(items.OrderBy(item => item.Name));
            }
        }

        /// <summary>
        /// Gets or sets the memory areas.
        /// </summary>
        /// <value>
        /// The memory areas.
        /// </value>
        public Enums.Memory.Areas MemoryAreas
        {
            get
            {
                if (!Computer.OperatingSystem.HasCombinedPageList)
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.CombinedPageList;

                if (!Computer.OperatingSystem.HasModifiedPageList)
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.ModifiedPageList;

                if (!Computer.OperatingSystem.HasRegistryHive)
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.RegistryCache;

                if (!Computer.OperatingSystem.HasStandbyList)
                {
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.StandbyList;
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.StandbyListLowPriority;
                }

                if (!Computer.OperatingSystem.HasSystemFileCache)
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.SystemFileCache;

                if (!Computer.OperatingSystem.HasWorkingSet)
                    Settings.MemoryAreas &= ~Enums.Memory.Areas.WorkingSet;

                return Settings.MemoryAreas;
            }
            set
            {
                try
                {
                    IsBusy = true;

                    if ((Settings.MemoryAreas & value) != 0)
                        Settings.MemoryAreas &= ~value;
                    else
                        Settings.MemoryAreas |= value;

                    switch (value)
                    {
                        case Enums.Memory.Areas.StandbyList:
                            if ((Settings.MemoryAreas & Enums.Memory.Areas.StandbyListLowPriority) != 0)
                                Settings.MemoryAreas &= ~Enums.Memory.Areas.StandbyListLowPriority;
                            break;

                        case Enums.Memory.Areas.StandbyListLowPriority:
                            if ((Settings.MemoryAreas & Enums.Memory.Areas.StandbyList) != 0)
                                Settings.MemoryAreas &= ~Enums.Memory.Areas.StandbyList;
                            break;
                    }

                    Settings.Save();

                    RaisePropertyChanged();
                    RaisePropertyChanged(() => CanOptimize);
                    RaisePropertyChanged(() => MemoryAreaItems);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets the memory usage thresholds.
        /// </summary>
        /// <value>
        /// The memory usage thresholds.
        /// </value>
        public List<byte> MemoryUsageThresholds { get; private set; }

        /// <summary>
        /// Gets or sets the optimization key.
        /// </summary>
        /// <value>
        /// The optimization key.
        /// </value>
        public Key OptimizationKey
        {
            get { return Settings.OptimizationKey; }
            set
            {
                try
                {
                    IsBusy = true;

                    RegisterOptimizationHotkey(Settings.OptimizationModifiers, value);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the optimization modifiers.
        /// </summary>
        /// <value>
        /// The optimization modifiers.
        /// </value>
        public ModifierKeys OptimizationModifiers
        {
            get { return Settings.OptimizationModifiers; }
            set
            {
                try
                {
                    IsBusy = true;

                    RegisterOptimizationHotkey(value, Settings.OptimizationKey);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the optimization progress percentage.
        /// </summary>
        /// <value>
        /// The optimization progress percentage.
        /// </value>
        public byte OptimizationProgressPercentage
        {
            get { return _optimizationProgressPercentage; }
            set
            {
                _optimizationProgressPercentage = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the optimization progress step.
        /// </summary>
        /// <value>
        /// The optimization progress step.
        /// </value>
        public string OptimizationProgressStep
        {
            get { return _optimizationProgressStep; }
            set
            {
                _optimizationProgressStep = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the optimization progress total.
        /// </summary>
        /// <value>
        /// The optimization progress total.
        /// </value>
        public byte OptimizationProgressTotal
        {
            get { return _optimizationProgressTotal; }
            set
            {
                _optimizationProgressTotal = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the optimization progress value.
        /// </summary>
        /// <value>
        /// The optimization progress value.
        /// </value>
        public byte OptimizationProgressValue
        {
            get { return _optimizationProgressValue; }
            set
            {
                _optimizationProgressValue = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Physical Memory Header
        /// </summary>
        public string PhysicalMemoryHeader
        {
            get
            {
                return string.Format(Localizer.Culture, "{0} ({1:0.#} {2})", Localizer.Strings.PhysicalMemory, Computer.Memory.Physical.Total.Value, Computer.Memory.Physical.Total.Unit);
            }
        }

        /// <summary>
        /// Lista processos Windows para ComboBox Process Exclusion List.
        ///
        /// PERFORMANCE (LAZY-LOAD CACHE):
        /// - Carrega processos 1x só (primeira abertura Settings)
        /// - Cache evita lag ComboBox (200+ processos)
        /// - Refresh manual via RefreshProcessesCommand
        ///
        /// BINDING XAML:
        /// - <ComboBox ItemsSource="{Binding Processes}" />
        ///
        /// FORMATO:
        /// - "chrome - Google Chrome"
        /// - "discord - Discord"
        /// - "spotify - Spotify"
        /// </summary>
        public ObservableCollection<string> Processes
        {
            get
            {
                // CACHE: Se já carregado, retorna imediatamente (evita lag)
                if (_processesCache != null)
                {
                    return _processesCache;
                }

                // CARREGAR PROCESSOS (1ª vez):
                var formattedProcesses = new List<string>();

                // Obter todos processos Windows, filtrando:
                // - Null processes
                // - SETTMemoryCleaner (próprio app)
                // - Processos já na exclusion list
                var runningProcesses = Process.GetProcesses()
                    .Where(p => p != null && !p.ProcessName.Equals(Constants.App.Name) && !Settings.ProcessExclusionList.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                foreach (var process in runningProcesses)
                {
                    try
                    {
                        // Nome processo: "chrome.exe" → "chrome"
                        var processName = process.ProcessName.ToLower(Localizer.Culture).Replace(".exe", string.Empty);
                        var displayName = processName;
                        var description = string.Empty;

                        // TENTAR OBTER DESCRIÇÃO AMIGÁVEL:

                        // 1. Tentar FileDescription (metadata .exe)
                        try
                        {
                            if (process.MainModule != null && !string.IsNullOrWhiteSpace(process.MainModule.FileName))
                            {
                                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(process.MainModule.FileName);
                                description = versionInfo.FileDescription;  // Ex: "Google Chrome"
                            }
                        }
                        catch
                        {
                            // 2. Fallback: Usar título janela
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
                                {
                                    description = process.MainWindowTitle;  // Ex: "Discord - #general"
                                }
                            }
                            catch { }  // Processo sem permissão? Ignorar
                        }

                        // FORMATAR DISPLAY: "chrome - Google Chrome"
                        if (!string.IsNullOrWhiteSpace(description) && !description.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} - {1}", processName, description);
                        }

                        formattedProcesses.Add(displayName);
                    }
                    catch
                    {
                        // Erro obter info? Adicionar só nome processo
                        formattedProcesses.Add(process.ProcessName.ToLower(Localizer.Culture).Replace(".exe", string.Empty));
                    }
                }

                // SALVAR CACHE:
                // - Remove duplicatas
                // - Ordena alfabeticamente
                _processesCache = new ObservableCollection<string>(formattedProcesses.Distinct().OrderBy(name => name));

                // Se processo selecionado não existe mais, selecionar primeiro
                if (!_processesCache.Contains(SelectedProcess, StringComparer.OrdinalIgnoreCase))
                    SelectedProcess = _processesCache.FirstOrDefault();

                return _processesCache;
            }
        }

        /// <summary>
        /// Gets or sets the process exclusion list.
        /// </summary>
        /// <value>
        /// The process exclusion list.
        /// </value>
        public ObservableCollection<string> ProcessExclusionList
        {
            get
            {
                return new ObservableCollection<string>(Settings.ProcessExclusionList);
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [run on low priority].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [run on low priority]; otherwise, <c>false</c>.
        /// </value>
        public bool RunOnLowPriority
        {
            get { return Settings.RunOnPriority == Enums.Priority.Low; }
            set
            {
                try
                {
                    IsBusy = true;

                    var priority = value ? Enums.Priority.Low : Enums.Priority.Normal;

                    App.SetPriority(priority);

                    Settings.RunOnPriority = priority;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [run on startup].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [run on startup]; otherwise, <c>false</c>.
        /// </value>
        public bool RunOnStartup
        {
            get { return Settings.RunOnStartup; }
            set
            {
                try
                {
                    IsBusy = true;

                    App.RunOnStartup(value);

                    Settings.RunOnStartup = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the selected process.
        /// </summary>
        /// <value>
        /// The selected process.
        /// </value>
        public string SelectedProcess
        {
            get { return _selectedProcess; }
            set
            {
                _selectedProcess = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets the setting items.
        /// </summary>
        /// <value>
        /// The setting items.
        /// </value>
        public ObservableCollection<ObservableItem<bool>> SettingItems
        {
            get
            {
                return new ObservableCollection<ObservableItem<bool>>
                (
                    new List<ObservableItem<bool>>
                    {
                       new ObservableItem<bool>(Localizer.Strings.AlwaysOnTop, () => AlwaysOnTop, value => AlwaysOnTop = value),
                       new ObservableItem<bool>(Localizer.Strings.AutoUpdate, () => AutoUpdate, value => AutoUpdate = value, Helper.IsAutoUpdateSupported),
                       new ObservableItem<bool>(Localizer.Strings.CloseAfterOptimization, () => CloseAfterOptimization, value => CloseAfterOptimization = value),
                       new ObservableItem<bool>(Localizer.Strings.CloseToTheNotificationArea, () => CloseToTheNotificationArea, value => CloseToTheNotificationArea = value),
                       new ObservableItem<bool>(Localizer.Strings.CreateStartMenuShortcut, () => CreateStartMenuShortcut, value => CreateStartMenuShortcut = value),
                       new ObservableItem<bool>(Localizer.Strings.RunOnLowPriority, () => RunOnLowPriority, value => RunOnLowPriority = value),
                       new ObservableItem<bool>(Localizer.Strings.RunOnStartup, () => RunOnStartup, value => RunOnStartup = value),
                       new ObservableItem<bool>(Localizer.Strings.ShowOptimizationNotifications, () => ShowOptimizationNotifications, value => ShowOptimizationNotifications = value),
                       new ObservableItem<bool>(Localizer.Strings.ShowVirtualMemory, () => ShowVirtualMemory, value => ShowVirtualMemory = value),
                       new ObservableItem<bool>(Localizer.Strings.StartMinimized, () => StartMinimized, value => StartMinimized = value)
                    }
                    .OrderBy(item => item.Name)
                );
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [show optimization notifications].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [show optimization notifications]; otherwise, <c>false</c>.
        /// </value>
        public bool ShowOptimizationNotifications
        {
            get { return Settings.ShowOptimizationNotifications; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.ShowOptimizationNotifications = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [show virtual memory].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [show virtual memory]; otherwise, <c>false</c>.
        /// </value>
        public bool ShowVirtualMemory
        {
            get { return Settings.ShowVirtualMemory; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.ShowVirtualMemory = value;
                    Settings.Save();

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [start minimized].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [start minimized]; otherwise, <c>false</c>.
        /// </value>
        public bool StartMinimized
        {
            get { return Settings.StartMinimized; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.StartMinimized = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating can [use hotkey].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [use hotkey]; otherwise, <c>false</c>.
        /// </value>
        public bool UseHotkey
        {
            get { return Settings.UseHotkey; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.UseHotkey = value;
                    Settings.Save();

                    if (value)
                        RegisterOptimizationHotkey(Settings.OptimizationModifiers, Settings.OptimizationKey);
                    else
                        UnregisterOptimizationHotkey();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets the title.
        /// </summary>
        public string Title
        {
            get
            {
                var beta = App.IsInDebugMode ? "\u200E? (BETA)\u200E" : null;
                return string.Format(Localizer.Culture, "{0}{1}", Constants.App.Title, beta);
            }
        }

        /// <summary>
        /// Gets or sets the color of the tray icon background.
        /// </summary>
        /// <value>
        /// The color of the tray icon background.
        /// </value>
        public Brush TrayIconBackgroundColor
        {
            get
            {
                if (App.IsInDesignMode)
                    return System.Windows.Media.Brushes.White;

                var brush = Settings.TrayIconBackgroundColor as System.Drawing.SolidBrush;

                if (brush == null)
                    return Brushes.FirstOrDefault(b => b.Color == Colors.White) ?? Brushes.First();

                return Brushes.FirstOrDefault(mediaBrush => mediaBrush.Color.IsEquals(brush.Color))
                    ?? Brushes.FirstOrDefault(b => b.Color == Colors.White)
                    ?? Brushes.First();
            }
            set
            {
                if (value == null)
                    return;

                Settings.TrayIconBackgroundColor = value.ToBrush();
                Settings.Save();

                NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the color of the tray icon on the danger level.
        /// </summary>
        /// <value>
        /// The color of the tray icon on the danger level.
        /// </value>
        public Brush TrayIconDangerColor
        {
            get
            {
                if (App.IsInDesignMode)
                    return System.Windows.Media.Brushes.White;

                var brush = Settings.TrayIconDangerColor as System.Drawing.SolidBrush;

                if (brush == null)
                    return Brushes.FirstOrDefault(b => b.Color == Colors.DarkRed) ?? Brushes.First();

                return Brushes.FirstOrDefault(mediaBrush => mediaBrush.Color.IsEquals(brush.Color))
                    ?? Brushes.FirstOrDefault(b => b.Color == Colors.DarkRed)
                    ?? Brushes.First();
            }
            set
            {
                if (value == null)
                    return;

                Settings.TrayIconDangerColor = value.ToBrush();
                Settings.Save();

                NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the tray icon danger level value.
        /// </summary>
        /// <value>
        /// The tray icon danger level value.
        /// </value>
        public byte TrayIconDangerLevel
        {
            get { return Settings.TrayIconDangerLevel; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.TrayIconDangerLevel = value;
                    Settings.Save();

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [tray icon optimize on middle mouse click].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [tray icon optimize on middle mouse click]; otherwise, <c>false</c>.
        /// </value>
        public bool TrayIconOptimizeOnMiddleMouseClick
        {
            get { return Settings.TrayIconOptimizeOnMiddleMouseClick; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.TrayIconOptimizeOnMiddleMouseClick = value;
                    Settings.Save();

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the color of the tray icon optimizing.
        /// </summary>
        /// <value>
        /// The color of the tray icon optimizing.
        /// </value>
        public Brush TrayIconOptimizingColor
        {
            get
            {
                if (App.IsInDesignMode)
                    return System.Windows.Media.Brushes.White;

                var brush = Settings.TrayIconOptimizingColor as System.Drawing.SolidBrush;

                if (brush == null)
                    return Brushes.FirstOrDefault(b => b.Color == Colors.DimGray) ?? Brushes.First();

                return Brushes.FirstOrDefault(mediaBrush => mediaBrush.Color.IsEquals(brush.Color))
                    ?? Brushes.FirstOrDefault(b => b.Color == Colors.DimGray)
                    ?? Brushes.First();
            }
            set
            {
                if (value == null)
                    return;

                Settings.TrayIconOptimizingColor = value.ToBrush();
                Settings.Save();

                NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets the tray icon items.
        /// </summary>
        /// <value>
        /// The tray icon items.
        /// </value>
        public ObservableCollection<ObservableItem<bool>> TrayIconItems
        {
            get
            {
                if (_trayIconItems == null)
                {
                    _trayIconItems = new ObservableCollection<ObservableItem<bool>>
                    (
                        new List<ObservableItem<bool>>
                        {
                            new ObservableItem<bool>(Localizer.Strings.OptimizeOnMiddleMouseClick, () => TrayIconOptimizeOnMiddleMouseClick, value => TrayIconOptimizeOnMiddleMouseClick = value),
                            new ObservableItem<bool>(Localizer.Strings.ShowMemoryUsage, () => TrayIconShowMemoryUsage, value => TrayIconShowMemoryUsage = value),
                            new ObservableItem<bool>(Localizer.Strings.UseTransparentBackground, () => TrayIconUseTransparentBackground, value => TrayIconUseTransparentBackground = value, TrayIconShowMemoryUsage)
                        }
                    );
                }
                return _trayIconItems;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [tray icon show memory usage].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [tray icon show memory usage]; otherwise, <c>false</c>.
        /// </value>
        public bool TrayIconShowMemoryUsage
        {
            get { return Settings.TrayIconShowMemoryUsage; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.TrayIconShowMemoryUsage = value;
                    Settings.Save();

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    RaisePropertyChanged();

                    var useTransparentBackgroundItem = TrayIconItems.FirstOrDefault(item => item.Name == Localizer.Strings.UseTransparentBackground);

                    if (useTransparentBackgroundItem != null)
                        useTransparentBackgroundItem.IsEnabled = value;
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the color of the tray icon text.
        /// </summary>
        /// <value>
        /// The color of the tray icon text.
        /// </value>
        public Brush TrayIconTextColor
        {
            get
            {
                if (App.IsInDesignMode)
                    return System.Windows.Media.Brushes.White;

                var brush = Settings.TrayIconTextColor as System.Drawing.SolidBrush;

                if (brush == null)
                    return Brushes.FirstOrDefault(b => b.Color == Colors.White) ?? Brushes.First();

                return Brushes.FirstOrDefault(mediaBrush => mediaBrush.Color.IsEquals(brush.Color))
                    ?? Brushes.FirstOrDefault(b => b.Color == Colors.White)
                    ?? Brushes.First();
            }
            set
            {
                if (value == null)
                    return;

                Settings.TrayIconTextColor = value.ToBrush();
                Settings.Save();

                NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether [tray icon use transparent background].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [tray icon use transparent background]; otherwise, <c>false</c>.
        /// </value>
        public bool TrayIconUseTransparentBackground
        {
            get { return Settings.TrayIconUseTransparentBackground; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.TrayIconUseTransparentBackground = value;
                    Settings.Save();

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Gets or sets the color of the tray icon on the warning level.
        /// </summary>
        /// <value>
        /// The color of the tray icon on the warning level.
        /// </value>
        public Brush TrayIconWarningColor
        {
            get
            {
                if (App.IsInDesignMode)
                    return System.Windows.Media.Brushes.White;

                var brush = Settings.TrayIconWarningColor as System.Drawing.SolidBrush;

                if (brush == null)
                    return Brushes.FirstOrDefault(b => b.Color == Colors.DarkRed) ?? Brushes.First();

                return Brushes.FirstOrDefault(mediaBrush => mediaBrush.Color.IsEquals(brush.Color))
                  ?? Brushes.FirstOrDefault(b => b.Color == Colors.DarkRed)
                  ?? Brushes.First();
            }
            set
            {
                if (value == null)
                    return;

                Settings.TrayIconWarningColor = value.ToBrush();
                Settings.Save();

                NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the tray icon warning level value.
        /// </summary>
        /// <value>
        /// The tray icon warning level value.
        /// </value>
        public byte TrayIconWarningLevel
        {
            get { return Settings.TrayIconWarningLevel; }
            set
            {
                try
                {
                    IsBusy = true;

                    Settings.TrayIconWarningLevel = value;
                    Settings.Save();

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    RaisePropertyChanged();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Virtual Memory Header
        /// </summary>
        public string VirtualMemoryHeader
        {
            get
            {
                return string.Format(Localizer.Culture, "{0} ({1:0.#} {2})", Localizer.Strings.VirtualMemory, Computer.Memory.Virtual.Total.Value, Computer.Memory.Virtual.Total.Unit);
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_cancellationTokenSource != null)
                {
                    try
                    {
                        _cancellationTokenSource.Cancel();
                    }
                    catch
                    {
                        // ignored
                    }

                    try
                    {
                        _cancellationTokenSource.Token.WaitHandle.WaitOne(100);
                    }
                    catch
                    {
                        // ignored
                    }

                    try
                    {
                        _cancellationTokenSource.Dispose();
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (_hotKeyService != null)
                {
                    try
                    {
                        _hotKeyService.Dispose();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        #endregion

        #region Commands

        /// <summary>
        /// Gets the add process to exclusion list.
        /// </summary>
        /// <value>
        /// The add process to exclusion list.
        /// </value>
        public ICommand AddProcessToExclusionListCommand { get; private set; }

        /// <summary>
        /// Gets the optimize command.
        /// </summary>
        /// <value>
        /// The optimize command.
        /// </value>
        public ICommand OptimizeCommand { get; private set; }

        /// <summary>
        /// Gets the remove process from exclusion list command.
        /// </summary>
        /// <value>
        /// The remove process from exclusion list command.
        /// </value>
        public ICommand RemoveProcessFromExclusionListCommand { get; private set; }

        /// <summary>
        /// Gets the reset settings to default configuration command.
        /// </summary>
        /// <value>
        /// The reset settings to default configuration command.
        /// </value>
        public ICommand ResetSettingsToDefaultConfigurationCommand { get; private set; }

        #endregion

        #region Actions

        /// <summary>
        /// Occurs when [on add process to exclusion list command completed].
        /// </summary>
        public event Action OnAddProcessToExclusionListCommandCompleted;

        /// <summary>
        /// Occurs when [on language change completed].
        /// </summary>
        public event Action OnLanguageChangeCompleted;

        /// <summary>
        /// Occurs when [on optimize command completed].
        /// </summary>
        public event Action OnOptimizeCommandCompleted;

        /// <summary>
        /// Occurs when [on remove process from exclusion list command completed].
        /// </summary>
        public event Action OnRemoveProcessFromExclusionListCommandCompleted;

        #endregion

        #region Methods

        /// <summary>
        /// Adds the process to exclusion list.
        /// </summary>
        /// <param name="process">The process.</param>
        private void AddProcessToExclusionList(string process)
        {
            try
            {
                IsBusy = true;

                if (!string.IsNullOrWhiteSpace(process))
                {
                    // Extrai nome original do processo (antes do " - ")
                    var processName = process;
                    if (process.Contains(" - "))
                    {
                        processName = process.Substring(0, process.IndexOf(" - ", StringComparison.Ordinal));
                    }

                    if (!Settings.ProcessExclusionList.Contains(processName, StringComparer.OrdinalIgnoreCase))
                    {
                        if (Settings.ProcessExclusionList.Add(processName))
                        {
                            Settings.Save();

                            // Só atualiza lista de exclusão (instantâneo)
                            // Processes será atualizado quando ComboBox reabrir naturalmente
                            RaisePropertyChanged(() => ProcessExclusionList);

                            if (OnAddProcessToExclusionListCommandCompleted != null)
                            {
                                WpfApplication.Current.Dispatcher.Invoke((Action)delegate
                                {
                                    OnAddProcessToExclusionListCommandCompleted();
                                });
                            }
                        }
                    }
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Monitor App Resources
        /// </summary>
        private void MonitorApp()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Check if it's busy
                    if (IsBusy)
                        continue;

                    // Delay
                    if (_cancellationTokenSource.Token.WaitHandle.WaitOne(60000))
                        break;

                    // Update app
                    Updater.Update();

                    // App priority
                    App.SetPriority(Settings.RunOnPriority);

                    // Auto Optimization
                    lock (_lockObject)
                    {
                        if (CanOptimize)
                        {
                            // Interval
                            if (Settings.AutoOptimizationInterval > 0 &&
                                DateTimeOffset.Now.Subtract(_lastAutoOptimizationByInterval).TotalHours >= Settings.AutoOptimizationInterval)
                            {
                                OptimizeAsync(Enums.Memory.Optimization.Reason.Schedule);

                                _lastAutoOptimizationByInterval = DateTimeOffset.Now;
                                continue;
                            }

                            // Memory usage
                            if (Settings.AutoOptimizationMemoryUsage > 0 &&
                                Computer.Memory.Physical.Free.Percentage < Settings.AutoOptimizationMemoryUsage &&
                                DateTimeOffset.Now.Subtract(_lastAutoOptimizationByMemoryUsage).TotalMinutes >= Constants.App.AutoOptimizationMemoryUsageInterval)
                            {
                                OptimizeAsync(Enums.Memory.Optimization.Reason.LowMemory);

                                _lastAutoOptimizationByMemoryUsage = DateTimeOffset.Now;
                                continue;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Debug(e);
                }
            }
        }

        /// <summary>
        /// Monitor Background Tasks
        /// </summary>
        private void MonitorAsync()
        {
            // Monitor App Resources
            try
            {
                ThreadPool.QueueUserWorkItem(_ => MonitorApp());
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }

            // Monitor Computer Resources
            try
            {
                ThreadPool.QueueUserWorkItem(_ => MonitorComputer());
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }
        }

        /// <summary>
        /// Monitor Computer Resources
        /// </summary>
        private void MonitorComputer()
        {
            // App priority
            App.SetPriority(Settings.RunOnPriority);

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Check if it's busy
                    if (IsBusy)
                        continue;

                    lock (_lockObject)
                    {
                        // Update memory info
                        Computer.Memory = _computerService.Memory;

                        RaisePropertyChanged(() => Computer);
                        RaisePropertyChanged(() => VirtualMemoryHeader);

                        NotificationService.Update(Computer.Memory, IsOptimizationRunning);
                    }

                    // Delay
                    if (_cancellationTokenSource.Token.WaitHandle.WaitOne(5000))
                        break;
                }
                catch (Exception e)
                {
                    Logger.Debug(e);
                }
            }
        }

        /// <summary>
        /// Called when [optimize progress is update].
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="step">The step.</param>
        private void OnOptimizeProgressUpdate(byte value, string step)
        {
            OptimizationProgressPercentage = (byte)(value * 100 / OptimizationProgressTotal);
            OptimizationProgressStep = step;
            OptimizationProgressValue = value;
        }

        /// <summary>
        /// Optimize
        /// </summary>
        /// <param name="reason">Optimization reason</param>
        private void Optimize(Enums.Memory.Optimization.Reason reason)
        {
            lock (_lockObject)
            {
                try
                {
                    IsBusy = true;
                    IsOptimizationRunning = true;

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    // App priority
                    App.SetPriority(Settings.RunOnPriority);

                    // Memory optimize
                    var tempPhysicalAvailable = Computer.Memory.Physical.Free.Bytes;
                    var tempVirtualAvailable = Computer.Memory.Virtual.Free.Bytes;

                    _computerService.Optimize(reason, Settings.MemoryAreas);

                    // Update memory info
                    Computer.Memory = _computerService.Memory;
                    RaisePropertyChanged(() => Computer);

                    // Notification
                    if (Settings.ShowOptimizationNotifications)
                    {
                        var physicalReleased = (Computer.Memory.Physical.Free.Bytes > tempPhysicalAvailable ? Computer.Memory.Physical.Free.Bytes - tempPhysicalAvailable : tempPhysicalAvailable - Computer.Memory.Physical.Free.Bytes).ToMemoryUnit();
                        var virtualReleased = (Computer.Memory.Virtual.Free.Bytes > tempVirtualAvailable ? Computer.Memory.Virtual.Free.Bytes - tempVirtualAvailable : tempVirtualAvailable - Computer.Memory.Virtual.Free.Bytes).ToMemoryUnit();

                        var message = Settings.ShowVirtualMemory
                            ? string.Format(Localizer.Culture, "{1}{0}{0}{2}: {3}{0}{4}: {5:0.#} {6}{0}{7}: {8:0.#} {9}", Environment.NewLine, Localizer.Strings.MemoryOptimized.ToUpper(Localizer.Culture), Localizer.Strings.Reason, reason.GetString(), Localizer.Strings.PhysicalMemory, physicalReleased.Key, physicalReleased.Value, Localizer.Strings.VirtualMemory, virtualReleased.Key, virtualReleased.Value)
                            : string.Format(Localizer.Culture, "{1}{0}{0}{2}: {3}{0}{4}: {5:0.#} {6}", Environment.NewLine, Localizer.Strings.MemoryOptimized.ToUpper(Localizer.Culture), Localizer.Strings.Reason, reason.GetString(), Localizer.Strings.PhysicalMemory, physicalReleased.Key, physicalReleased.Value);

                        Notify(message);
                    }
                }
                finally
                {
                    IsOptimizationRunning = false;
                    IsBusy = false;

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    // Raise the event after IsOptimizationRunning is set to false
                    // Use BeginInvoke to ensure it runs after all property changes propagate
                    WpfApplication.Current.Dispatcher.BeginInvoke((Action)(() =>
                    {
                        // Force command manager to re-evaluate CanExecute on all commands
                        CommandManager.InvalidateRequerySuggested();
                    }), System.Windows.Threading.DispatcherPriority.Normal);

                    // Raise completion event with lower priority to ensure commands are refreshed first
                    if (OnOptimizeCommandCompleted != null)
                    {
                        WpfApplication.Current.Dispatcher.BeginInvoke((Action)(() =>
                         {
                             OnOptimizeCommandCompleted();
                         }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }
            }
        }

        /// <summary>
        /// MÉTODO PRINCIPAL: Inicia otimização RAM (async).
        ///
        /// CHAMADO POR:
        /// - OptimizeCommand (botão OPTIMIZE clicado)
        /// - MonitorComputer() (auto-otimização por timer/threshold RAM)
        /// - RegisterOptimizationHotkey() (Ctrl+Alt+O pressionado)
        ///
        /// FLUXO:
        /// 1. Valida se já está otimizando (evita duplicatas)
        /// 2. Inicializa ProgressBar (steps = áreas selecionadas)
        /// 3. Dispara Optimize() em background thread
        /// 4. ComputerService limpa RAM via Win32 API
        /// 5. Notificação toast: "Freed: X GB"
        /// </summary>
        /// <param name="reason">Motivo: Manual (botão), Schedule (timer), LowMemory (threshold RAM)</param>
        private void OptimizeAsync(Enums.Memory.Optimization.Reason reason)
        {
            try
            {
                // Validação: Já otimizando? Sair (evita execuções duplicadas)
                if (IsOptimizationRunning)
                    return;

                // Inicializar ProgressBar:
                OptimizationProgressStep = Localizer.Strings.Optimize;  // "Optimizing..."
                OptimizationProgressValue = 0;                          // 0%

                // Total steps = Nº áreas selecionadas (count bits Settings.MemoryAreas)
                OptimizationProgressTotal = (byte)(new BitArray(new[] { (int)Settings.MemoryAreas }).OfType<bool>().Count(x => x) + 1);

                // Executar otimização em background thread (não bloqueia UI)
                ThreadPool.QueueUserWorkItem(_ => Optimize(reason));
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }
        }

        /// <summary>
        /// Registers the optimization hotkey.
        /// </summary>
        /// <param name="modifiers">The modifiers.</param>
        /// <param name="key">The key.</param>
        private void RegisterOptimizationHotkey(ModifierKeys modifiers, Key key)
        {
            UnregisterOptimizationHotkey();

            Settings.OptimizationKey = key;
            Settings.OptimizationModifiers = modifiers;

            var hotKey = new Hotkey(Settings.OptimizationModifiers, Settings.OptimizationKey);

            IsOptimizationKeyValid = _hotKeyService.Register(hotKey, () => OptimizeAsync(Enums.Memory.Optimization.Reason.Manual));

            if (!_isReiniziliating && !IsOptimizationKeyValid)
            {
                var message = string.Format(Localizer.Culture, Localizer.Strings.HotkeyIsInUseByOperatingSystem, hotKey);

                Logger.Warning(message);
                NotificationService.Notify(message);

                return;
            }

            Settings.Save();

            RaisePropertyChanged(() => OptimizationKey);
            RaisePropertyChanged(() => OptimizationModifiers);
        }

        /// <summary>
        /// Reinitializes app after system resume from hibernation.
        /// </summary>
        public void ReinitializeAfterHibernation()
        {
            try
            {
                lock (_lockObject)
                {
                    _isReiniziliating = true;

                    if (UseHotkey)
                        RegisterOptimizationHotkey(Settings.OptimizationModifiers, Settings.OptimizationKey);

                    Computer.Memory = _computerService.Memory;

                    NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                    RaisePropertyChanged(string.Empty);

                    App.ReleaseMemory();
                }
            }
            catch (Exception e)
            {
                Logger.Error("Error after system resume from hibernation: " + e.GetMessage());
            }
            finally
            {
                _isReiniziliating = false;
            }
        }

        /// <summary>
        /// Removes the process from exclusion list.
        /// </summary>
        /// <param name="process">The process.</param>
        private void RemoveProcessFromExclusionList(string process)
        {
            try
            {
                IsBusy = true;

                if (Settings.ProcessExclusionList.Remove(process))
                    Settings.Save();

                // Só atualiza lista de exclusão (instantâneo)
                // Processes será atualizado quando ComboBox reabrir naturalmente
                RaisePropertyChanged(() => ProcessExclusionList);

                if (OnRemoveProcessFromExclusionListCommandCompleted != null)
                {
                    WpfApplication.Current.Dispatcher.Invoke((Action)delegate
                    {
                        OnRemoveProcessFromExclusionListCommandCompleted();
                    });
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Resets settings to the default configuration.
        /// </summary>
        private void ResetSettingsToDefaultConfiguration()
        {
            try
            {
                IsBusy = true;

                Settings.Reset(true);
                ThemeManager.Theme = Enums.Theme.Dark;

                FontSize = Settings.FontSize;
                OptimizationKey = Settings.OptimizationKey;
                OptimizationModifiers = Settings.OptimizationModifiers;

                _trayIconItems = null;

                NotificationService.Initialize();
                NotificationService.Update(Computer.Memory, IsOptimizationRunning);

                RaisePropertyChanged(string.Empty);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Unregisters the optimization hotkey.
        /// </summary>
        private void UnregisterOptimizationHotkey()
        {
            _hotKeyService.Unregister(new Hotkey(Settings.OptimizationModifiers, Settings.OptimizationKey));

            IsOptimizationKeyValid = true;
        }

        #endregion
    }
}
