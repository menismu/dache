using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dache.Core.Communication
{
    /// <summary>
    /// It defines the operations of cache host discovers.
    /// </summary>
    public interface ICacheHostAutoDetectManager
    {
        /// <summary>
        /// Runs the discovery of cache hots process.
        /// </summary>
        void Run();

        /// <summary>
        /// Tries to stop the discovery of cache hosts process internally.
        /// </summary>
        void TryStop();
    }
}
