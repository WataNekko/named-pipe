using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace WataNekko.IO.Pipes
{
    /// <summary>
    /// Represents a named pipe client.
    /// </summary>
    public sealed class NamedPipeClient : NamedPipe
    {
        // ---------- Default values ---------- //
        private const string defaultServerName = ".";

        // ---------- Properties ---------- //
        /// <summary>
        /// Gets or sets the name of the remote computer to connect to, or "." to specify the local computer.
        /// </summary>
        /// <remarks>
        /// Changes are only applied after <see cref="NamedPipe.ConnectAsync()"/> successfully returns.
        /// </remarks>
        /// <value>A <see cref="string"/> representing the name of the remote computer to connect to, or "." to specify the local computer. The default is ".".</value>
        public string ServerName { get; set; }

        // ---------- Constructor ---------- //
        public NamedPipeClient(string pipeName, string serverName = defaultServerName, int bufferSize = defaultBufferSize) : base(pipeName, bufferSize)
            => ServerName = serverName;

        // ---------- Methods ---------- //
        protected override Task ConnectingAsync(CancellationToken cancellationToken)
        {
            var clPipe = new NamedPipeClientStream(ServerName, PipeName, Direction, Options);
            pipeStream = clPipe;
            return clPipe.ConnectAsync(cancellationToken);
        }
    }
}
