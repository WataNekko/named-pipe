using System.IO.Pipes;
using System.Threading;

namespace WataNekko.IO.Pipes
{
    internal static class ExtensionMethods
    {
        /// <summary>
        /// Implements a <see cref="CancellationToken"/> in addition to the <see cref="PipeStream.Read(byte[], int, int)"/> method.
        /// </summary>
        public static int Read(this PipeStream pipeStream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (cancellationToken.Register(() => CancelIoEx(pipeStream.SafePipeHandle)))
            {
                return pipeStream.Read(buffer, offset, count);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafePipeHandle handle, System.IntPtr _ = default);
    }
}
