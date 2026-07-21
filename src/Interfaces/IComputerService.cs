using System;

namespace SETTMemoryCleaner
{
    /// <summary>
    /// IComputerService
    /// </summary>
    /// <seealso cref="IMemoryService" />
    /// <seealso cref="IOperatingSystem" />
    public interface IComputerService : IMemoryService, IOperatingSystem
    {
        /// <summary>
        /// Occurs when [optimize progress is update].
        /// </summary>
        event Action<byte, string> OnOptimizeProgressUpdate;
    }
}
