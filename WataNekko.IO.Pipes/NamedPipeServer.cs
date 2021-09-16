using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace WataNekko.IO.Pipes
{
    /// <summary>
    /// Represents a named pipe server
    /// </summary>
    public sealed class NamedPipeServer : NamedPipe
    {
        // ---------- Default values ---------- //
        /// <value>1</value>
        private const int defaultMaxNumberOfServerInstances = 1;
        /// <value><see cref="PipeTransmissionMode.Byte"/></value>
        private const PipeTransmissionMode defaultTransmissionMode = PipeTransmissionMode.Byte;

        // ---------- Properties ---------- //
        /// <summary>
        /// Gets or sets a value representing the maximum number of server instances that can share the same name.
        /// </summary>
        /// <remarks>
        /// Changes are only applied after <see cref="NamedPipe.ConnectAsync"/> successfully returns.
        /// </remarks>
        /// <value>
        /// An <see cref="Int32"/> representing the maximum number of server instances that can share the same name.
        /// This value can be <see cref="NamedPipeServerStream.MaxAllowedServerInstances">MaxAllowedServerInstances</see>. The default is <inheritdoc cref="defaultMaxNumberOfServerInstances"/>.
        /// </value>
        public int MaxNumberOfServerInstances { get; set; } = defaultMaxNumberOfServerInstances;

        /// <summary>
        /// Gets or sets the enum value specifying the transmission mode of the pipe.
        /// </summary>
        /// <remarks>
        /// Changes are only applied after <see cref="NamedPipe.ConnectAsync"/> successfully returns.
        /// </remarks>
        /// <value>A <see cref="PipeTransmissionMode"/> value. The default is <inheritdoc cref="defaultTransmissionMode"/></value>
        public PipeTransmissionMode TransmissionMode { get; set; } = defaultTransmissionMode;

        // ---------- Constructor ---------- //
        public NamedPipeServer(string pipeName, int bufferSize = defaultBufferSize) : base(pipeName, bufferSize) { }

        // ---------- Methods ---------- //
        protected override async Task ConnectingAsync(CancellationToken cancellationToken)
        {
            var svPipe = new NamedPipeServerStream(PipeName, Direction, MaxNumberOfServerInstances, TransmissionMode, Options);
            pipeStream = svPipe;

            using (cancellationToken.Register(() =>
            {
                using (var dummy = new NamedPipeClientStream(PipeName))
                {
                    // Since cancellation tokens don't work on already waiting servers,
                    // we create a dummy client and connect to end the wait task.
                    try
                    {
                        dummy.Connect(100);
                    }
                    catch
                    {
                    }
                }
            }))
            {
                await svPipe.WaitForConnectionAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
