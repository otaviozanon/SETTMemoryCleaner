using System;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace SETTMemoryCleaner
{
    /// <summary>
    /// Enumerators
    /// </summary>
    public static class Enums
    {
        public static class Dialog
        {
            public enum Button
            {
                None,
                Yes,
                No
            }
        }

        public static class Icon
        {
            public enum Notification
            {
                None,
                Information,
                Warning,
                Error
            }
        }

        public static class Log
        {
            [Flags]
            public enum Levels
            {
                Debug = 1,
                Information = 2,
                Warning = 4,
                Error = 8
            }
        }

        public static class Memory
        {
            [Flags]
            public enum Areas
            {
                None = 0,
                CombinedPageList = 1,
                ModifiedFileCache = 2,
                ModifiedPageList = 4,
                RegistryCache = 8,
                StandbyList = 16,
                StandbyListLowPriority = 32,
                SystemFileCache = 64,
                WorkingSet = 128,
            }

            public static class Optimization
            {
                public enum Reason
                {
                    LowMemory,
                    Manual,
                    Schedule
                }
            }

            public enum Unit { B, KB, MB, GB, TB, PB, EB, ZB, YB }
        }

        public enum Priority
        {
            Low,
            Normal,
            High
        }
        public enum StartupType
        {
            Normal,
            Silent
        }

        public enum Theme
        {
            Dark,
            Light
        }
    }
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member