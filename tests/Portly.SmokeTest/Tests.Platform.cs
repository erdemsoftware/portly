using System.IO;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "Platform" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_Platform(Settings s, string www)
        {
            await Task.Yield();
        }
    }
}
