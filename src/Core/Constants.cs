using System;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace SETTMemoryCleaner
{
    /// <summary>
    /// Constantes aplicação.
    ///
    /// ORGANIZAÇÃO:
    /// - App: Constantes gerais (nome, versão, paths)
    /// - WinAPI: Constantes Win32 API (códigos, flags, valores)
    ///
    /// USADO POR: Todos componentes
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Constantes aplicação geral
        /// </summary>
        public static class App
        {
            // INTERVALO AUTO-OTIMIZAÇÃO: Mínimo 5 minutos entre execuções por threshold RAM
            // (evita otimizações muito frequentes que degradam performance)
            public const int AutoOptimizationMemoryUsageInterval = 5; // Minute

            // AUTO-UPDATE: Verifica nova versão a cada 24 horas
            public const int AutoUpdateInterval = 24; // Hour

            // EMBEDDED RESOURCES: Caminho base recursos embarcados .exe
            // Ex: "SETTMemoryCleaner.Resources.Localization.English.json"
            public const string EmbeddedResourcePath = "SETTMemoryCleaner.Resources.";
            public const string EmbeddedResourcePathExtension = ".json";

            // GUID único app (single-instance check)
            public const string Id = "C7F29A45-8B3E-4D2F-9A1C-5E7B2D4F8C6A";

            // Strong name key file (assinatura assembly)
            public const string KeyFile = "SETTMemoryCleaner.snk";

            // Licença open-source
            public const string License = "GPL-3.0";

            // Paths recursos embarcados
            public const string LocalizationResourcePath = EmbeddedResourcePath + "Localization.";

            // Nome processo (task manager, single-instance)
            public const string Name = "SETTMemoryCleaner";

            // Nome atalho Start Menu
            public const string Shortcut = "SETT Memory Cleaner.lnk";

            // Path temas (Dark/Light)
            public const string ThemesResourcePath = EmbeddedResourcePath + "Themes.";

            // Título janela
            public const string Title = "SETT Memory Cleaner";

            // Formato versão: "1.0.0"
            public const string VersionFormat = "{0}.{1}.{2}";

            public static class Author
            {
                public const string Name = "Igor Mundstein";
            }

            public static class Certificate
            {
                public static class Release
                {
                    public const string Thumbprint = "9D201FB199626ABE7DA32FBE47013FC023670F9B";
                }

                public static class TestCertificate
                {
                    public const string Thumbprint = "2187092935C12F90727B29AD6913A7F89817B942";
                }
            }

            public static class Defaults
            {
                public static readonly string Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            public static class Donation
            {
                public static readonly Uri BitcoinUri = new Uri("https://www.blockchain.com/explorer/addresses/btc/bc1qu884q5r2uqugvdhyk8l6waakumeve7jykqp7ap");
                public static readonly Uri EthereumUri = new Uri("https://www.blockchain.com/explorer/addresses/eth/0xb71A94733B0578D155D9A765E0d2C4dA0f44156d");
                public static readonly Uri GitHubSponsorUri = new Uri("https://github.com/sponsors/IgorMundstein");
                public static readonly Uri KofiUri = new Uri("https://ko-fi.com/igormundstein");
            }

            public static class Registry
            {
                public static class Key
                {
                    public const string ProcessExclusionList = @"SOFTWARE\WinMemoryCleaner\ProcessExclusionList";
                    public const string Settings = @"SOFTWARE\WinMemoryCleaner";
                }
            }

            public static class Repository
            {
                private const string GitHub = "https://github.com/IgorMundstein/WinMemoryCleaner";
                private const string GitHubRaw = "https://raw.githubusercontent.com/IgorMundstein/WinMemoryCleaner/main";

                public static readonly Uri AboutUri = new Uri(GitHub + "?tab=readme-ov-file#windows-memory-cleaner");
                public static readonly Uri AssemblyInfoUri = new Uri(GitHubRaw + "/src/Properties/AssemblyInfo.cs");
                public static readonly Uri DownloadUri = new Uri(GitHub + "?tab=readme-ov-file#-download");
                public static readonly Uri LatestExeUri = new Uri(GitHub + "/releases/latest/download/WinMemoryCleaner.exe");
                public static readonly Uri Uri = new Uri(GitHub);
            }
        }

        public static class WinAPI
        {
            public static class Console
            {
                public const int AttachParentProcess = -1; // ATTACH_PARENT_PROCESS
                public const int StdOutputHandle = -11; // STD_OUTPUT_HANDLE
            }

            public static class DesktopWindowManager
            {
                public static class Attribute
                {
                    public const int BorderColor = 34;
                    public const int WindowCornerPreference = 33;
                }

                public static class Value
                {
                    public const int WindowCornerPreferenceRound = 2;
                }
            }

            public static class Drive
            {
                public const int FsctlDiscardVolumeCache = 589828; // 0x00090054 - FSCTL_DISCARD_VOLUME_CACHE
                public const int IoControlResetWriteOrder = 589832; // 0x000900F8 - FSCTL_RESET_WRITE_ORDER
            }

            public static class File
            {
                public const int FlagsNoBuffering = 536870912; // 0x20000000 - FILE_FLAG_NO_BUFFERING
            }

            public static class Keyboard
            {
                public const int WmHotkey = 786; // 0x312
            }

            public static class Locale
            {
                public static class Name
                {
                    public const string English = "en";
                    public const string PortugueseBrazil = "pt-BR";
                    public const string PortuguesePortugal = "pt-PT";
                    public const string SimplifiedChinese = "zh-Hans";
                    public const string TraditionalChinese = "zh-Hant";
                }
            }

            public static class Privilege
            {
                public const string SeDebugName = "SeDebugPrivilege"; // Required to debug and adjust the memory of a process owned by another account. User Right: Debug programs.
                public const string SeIncreaseQuotaName = "SeIncreaseQuotaPrivilege"; // Required to increase the quota assigned to a process. User Right: Adjust memory quotas for a process.
                public const string SeProfSingleProcessName = "SeProfileSingleProcessPrivilege"; // Required to gather profiling information for a single process. User Right: Profile single process.
            }

            public static class PrivilegeAttribute
            {
                public const int Enabled = 2;
            }

            public static class ShowWindow
            {
                public const int Restore = 9; // SW_RESTORE
            }

            public static class SystemErrorCode
            {
                public const int ErrorAccessDenied = 5; // (ERROR_ACCESS_DENIED) Access is denied
                public const int ErrorSuccess = 0; // (ERROR_SUCCESS) The operation completed successfully
            }

            public static class SystemInformationClass
            {
                public const int SystemCombinePhysicalMemoryInformation = 130; // 0x82
                public const int SystemFileCacheInformation = 21; // 0x15
                public const int SystemMemoryListInformation = 80; // 0x50
                public const int SystemRegistryReconciliationInformation = 155; // 0x9B
            }

            public static class SystemMemoryListCommand
            {
                public const int MemoryEmptyWorkingSets = 2;
                public const int MemoryFlushModifiedList = 3;
                public const int MemoryPurgeLowPriorityStandbyList = 5;
                public const int MemoryPurgeStandbyList = 4;
            }
        }
    }
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
