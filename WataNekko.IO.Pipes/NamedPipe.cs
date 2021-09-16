using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace WataNekko.IO.Pipes
{
    /// <summary>
    /// Base class for <see cref="NamedPipeServer"/> and <see cref="NamedPipeClient"/>
    /// </summary>
    public abstract class NamedPipe
    {
        // ---------- Default values ---------- //
        /// <value>1024</value>
        protected const int defaultBufferSize = 1024;
        /// <value><c>true</c></value>
        private const bool defaultOverwriteBufferDataWhenFull = true;
        /// <summary>
        /// Represents the direction of the pipe of a <see cref="NamedPipe"/> object.
        /// </summary>
        public const PipeDirection Direction = PipeDirection.InOut;
        /// <summary>
        /// Represents the pipe creation options of a <see cref="NamedPipe"/> object.
        /// </summary>
        public const PipeOptions Options = PipeOptions.Asynchronous;
        /// <value>100</value>
        private const int defaultWriteTimeout = 100;

        // ---------- Members ---------- //
        protected PipeStream pipeStream;

        private byte[] _inBuffer;
        private int _readStart = 0; // start position of unread bytes in the buffer (inclusive)
        private int _readEnd = 0; // end position of unread bytes in the buffer (exclusive)
        private const int fullIndicatorConst = -1;

        private bool _overwriteBufferDataWhenFull = defaultOverwriteBufferDataWhenFull;
        private bool _readLoopRunning = false;
        private int _readQueue = 0;
        private CancellationTokenSource _readCancel;

        private readonly object _pipeLock = new object();
        private readonly object _bufferLock = new object();

        private int _writeTimeout = defaultWriteTimeout;

        // ---------- Events ---------- //
        /// <summary>
        /// Indicates that data has been received through a pipe represented by the <see cref="NamedPipe"/> object.
        /// </summary>
        public event EventHandler DataReceived;
        /// <summary>
        /// Indicates that connection with the other end of the pipe has been lost.
        /// </summary>
        /// <remarks>
        /// This event is only fired when the other end is the one that explicitly disconnects.
        /// </remarks>
        public event EventHandler ConnectionLost;

        // ---------- Properties ---------- //
        /// <summary>
        /// Gets or sets the name of the pipe.
        /// </summary>
        /// <remarks>
        /// Changes are only applied after <see cref="ConnectAsync"/> successfully returns.
        /// </remarks>
        public string PipeName { get; set; }

        /// <summary>
        /// Gets or sets the size, in bytes, of the inbound buffer for the pipe.
        /// </summary>
        /// <remarks>
        /// Attempts to change the size when the pipe is connected will result in an exception.
        /// </remarks>
        /// <value>The size in bytes of the input buffer. The default is <inheritdoc cref="defaultBufferSize"/>.</value>
        /// <exception cref="InvalidOperationException">The pipe is connected.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The <see cref="InBufferSize"/> value is less than or equal to zero.</exception>
        public int InBufferSize
        {
            get => _inBuffer.Length;
            set
            {
                if (IsConnected)
                {
                    throw new InvalidOperationException("Attempt to change the buffer size when the pipe is connected.");
                }
                if (_inBuffer?.Length == value)
                {
                    return;
                }
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException("Buffer size must be positive.");
                }

                _inBuffer = new byte[value];
            }
        }

        /// <summary>
        /// Gets a value indicating whether the pipe is connected.
        /// </summary>
        public bool IsConnected => pipeStream != null && pipeStream.IsConnected;

        /// <summary>
        /// Gets or sets the value indicating whether new data should overwrite oldest data
        /// when the buffer is full.
        /// </summary>
        /// <remarks>
        /// When set to true, oldest unread data will be lost if new data is available. Default value is true.
        /// Attempts to change this when the pipe is connected will result in an exception.
        /// </remarks>
        /// <value>A <see cref="bool"/> indicating whether new data should overwrite oldest data when the buffer is full. The default is <inheritdoc cref="defaultOverwriteBufferDataWhenFull"/></value>
        /// <exception cref="InvalidOperationException">The pipe is connected.</exception>
        public bool OverwriteBufferDataWhenFull
        {
            get => _overwriteBufferDataWhenFull;
            set
            {
                if (IsConnected)
                {
                    throw new InvalidOperationException("Attempt to change buffer option when the pipe is connected.");
                }

                _overwriteBufferDataWhenFull = value;
            }
        }

        /// <summary>
        /// Gets the number of bytes of data available to read in the input buffer.
        /// </summary>
        /// <returns>An <see cref="Int32"/> value between zero and <see cref="InBufferSize"/>.</returns>
        public int BytesInBuffer => (_readEnd == fullIndicatorConst)
            ? _inBuffer.Length
            : ((_readStart <= _readEnd) ? (_readEnd - _readStart) : (_inBuffer.Length - _readStart + _readEnd));

        /// <summary>
        /// Gets or sets the number of milliseconds before a time-out occurs when a write operation does not finish.
        /// </summary>
        /// <remarks>
        /// Attempts to change this value when the pipe is connected will result in an exception.
        /// It is possible but not recommended to set the timeout to <see cref="Timeout.Infinite">Infinite</see>
        /// as the <see cref="Write"/> method can block other operations until it finishes or times out.
        /// </remarks>
        /// <value>The number of milliseconds before a time-out occurs. The default is <inheritdoc cref="defaultWriteTimeout"/>.</value>
        /// <exception cref="InvalidOperationException">The pipe is connected.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The <see cref="WriteTimeout"/> value is less than zero and not equal to <see cref="Timeout.Infinite">Infinite</see>.</exception>
        public int WriteTimeout
        {
            get => _writeTimeout;
            set
            {
                if (IsConnected)
                {
                    throw new InvalidOperationException("Attempt to change write timeout when the pipe is connected.");
                }
                if (value < 0 && value != Timeout.Infinite)
                {
                    throw new ArgumentOutOfRangeException("Timeout must be non-negative or equal to Timeout.Infinite");
                }

                _writeTimeout = value;
            }
        }

        // ---------- Constructor ---------- //
        protected NamedPipe(string pipeName, int bufferSize) => (PipeName, InBufferSize) = (pipeName, bufferSize);

        // ---------- Methods ---------- //
        /// <summary>
        /// Asynchronously attempts to connect the pipe and waits indefinitely until succeeds.
        /// </summary>
        /// <returns>A task that represents the asynchronous connect operation.</returns>
        public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

        /// <summary>
        /// Asynchronously attempts to connect the pipe and monitors cancellation requests.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None">None</see>.</param>
        /// <returns>A task that represents the asynchronous connect operation.</returns>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ConnectingAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Disconnect();
                return;
            }

            _readStart = _readEnd = 0; // reset buffer
            _readCancel = new CancellationTokenSource();

            _readLoopRunning = true;
            Thread readLoopThread = new Thread(InternalReadLoop) { IsBackground = true };
            readLoopThread.Start();
        }

        protected abstract Task ConnectingAsync(CancellationToken cancellationToken);

        private void InternalReadLoop()
        {
            while (_readLoopRunning)
            {
                if ((_readEnd == fullIndicatorConst) && !_overwriteBufferDataWhenFull)
                {
                    continue;
                }

                // wait until queue is empty before taking the lock
                while (_readQueue > 0) { }

                lock (_bufferLock)
                {
                    ref int readFrom = ref ((_readEnd == fullIndicatorConst) ? ref _readStart : ref _readEnd);
                    int readTo = (_overwriteBufferDataWhenFull || (_readStart <= _readEnd)) ? _inBuffer.Length : _readStart;
                    int numBytesRead;

                    try
                    {
                        // pipeStream.Read blocks until new data arrives or the other end disconnects
                        numBytesRead = pipeStream.Read(_inBuffer, readFrom, readTo - readFrom, _readCancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // release the lock and continue the loop when user calls public Read methods,
                        // which request cancellation of the blocking pipeStream.Read
                        continue;
                    }
                    catch (Exception ex) when
                    (
                        ex is NullReferenceException || // pipeStream dereferenced
                        ex is ObjectDisposedException || // pipeStream closed/disposed
                        ex is InvalidOperationException // pipeStream (server) disconnected
                    )
                    {
                        // catch exceptions thrown when another thread disconnects pipeStream
                        // after the loop is entered and before pipeStream.Read is called
                        numBytesRead = 0;
                    }

                    if (numBytesRead == 0) // connection lost
                    {
                        if (InternalDisconnect())
                        {
                            Task.Run(OnConnectionLost);
                        }
                        return;
                    }

                    if ((_readEnd != fullIndicatorConst) && (_readEnd < _readStart) && (numBytesRead > _readStart - _readEnd))
                    {
                        // case of Overwrite == true, the buffer becoming full after the reading and data having been overwritten
                        _readStart = _readEnd + numBytesRead;
                        if (_readStart >= _inBuffer.Length)
                        {
                            _readStart = 0; // rollover if needed
                        }
                        _readEnd = fullIndicatorConst;
                    }
                    else
                    {
                        // general cases
                        readFrom += numBytesRead;
                        if (readFrom >= _inBuffer.Length)
                        {
                            readFrom = 0; // rollover if needed
                        }
                        if (_readStart == _readEnd)
                        {
                            _readEnd = fullIndicatorConst;
                        }
                    }
                }

                Task.Run(OnDataReceived);
            }
        }

        /// <summary>
        /// Disconnects the current connection. Does nothing if the pipe is already disconnected.
        /// </summary>
        public void Disconnect() => InternalDisconnect();

        // This method disconnects the pipe. Returns false if the pipe is already disconnected, otherwise true.
        private bool InternalDisconnect()
        {
            lock (_pipeLock)
            {
                if (pipeStream != null)
                {
                    _readLoopRunning = false;

                    if (pipeStream.IsConnected)
                    {
                        // cancel all pending IO operations
                        ExtensionMethods.CancelIoEx(pipeStream.SafePipeHandle);

                        (pipeStream as NamedPipeServerStream)?.Disconnect();
                    }
                    pipeStream.Dispose();
                    pipeStream = null;

                    _readCancel?.Dispose();
                    _readCancel = null;

                    return true;
                }

                return false;
            }
        }

        protected virtual void OnConnectionLost() => ConnectionLost?.Invoke(this, EventArgs.Empty);

        protected virtual void OnDataReceived() => DataReceived?.Invoke(this, EventArgs.Empty);

        // Wrapper for Read methods to ensure thread safety.
        private int SafeRead(Action validation, Func<int> readCore)
        {
            validation();

            Interlocked.Increment(ref _readQueue);
            // request cancellation of the internal blocking read to release the lock
            _readCancel.Cancel();
            lock (_bufferLock)
            {
                try
                {
                    return readCore();
                }
                finally
                {
                    Interlocked.Decrement(ref _readQueue);
                    if (_readQueue == 0 && _readCancel != null)
                    {
                        // reset CancellationTokenSource for next Read
                        _readCancel.Dispose();
                        _readCancel = new CancellationTokenSource();
                    }
                }
            }
        }

        /// <summary>
        /// Reads a number of bytes from the <see cref="NamedPipe"/> input buffer and writes
        /// those bytes into a byte array at the specified offset.
        /// </summary>
        /// <param name="buffer">The byte array to write the input to.</param>
        /// <param name="offset">The offset in <paramref name="buffer"/> at which to write the bytes.</param>
        /// <param name="count">The maximum number of bytes to read. Fewer bytes are read if <paramref name="count"/> is greater than the number of bytes in the input buffer.</param>
        /// <returns>The number of bytes read.</returns>
        /// <exception cref="InvalidOperationException">The specified pipe is not connected.</exception>
        /// <exception cref="ArgumentNullException">The buffer passed is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The <paramref name="offset"/> or <paramref name="count"/> parameters are outside a valid region of the <paramref name="buffer"/> being passed. Either <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
        /// <exception cref="ArgumentException"><paramref name="offset"/> plus <paramref name="count"/> is greater than the length of the <paramref name="buffer"/>.</exception>
        public int Read(byte[] buffer, int offset, int count)
        {
            return SafeRead(() =>
            {
                ThrowIfPipeNotConnected();
                ThrowIfInvalidArgument(buffer, offset, count);
            }, () =>
            {
                int totalRead = Math.Min(BytesInBuffer, count);
                if (totalRead == 0)
                {
                    return 0;
                }

                // make sure it copies within bound
                count = Math.Min(totalRead, _inBuffer.Length - _readStart);
                Array.Copy(_inBuffer, _readStart, buffer, offset, count);

                // update buffer read positions
                if (_readEnd == fullIndicatorConst)
                {
                    _readEnd = _readStart;
                }
                _readStart += count;
                if (_readStart >= _inBuffer.Length)
                {
                    _readStart = 0; // rollover if needed
                }

                if (count < totalRead) // more data left
                {
                    offset += count; // new offset
                    count = totalRead - count; // the rest of the data
                    Array.Copy(_inBuffer, _readStart, buffer, offset, count);

                    _readStart = count; // update pos
                }

                if (_readStart == _readEnd)
                {
                    _readStart = _readEnd = 0; // attempt to reset the buffer
                }

                return totalRead;
            });
        }

        /// <summary>
        /// Reads a number of bytes from the <see cref="NamedPipe"/> input buffer and writes
        /// those bytes into a byte array at the specified offset. Returns 0 if the pipe is
        /// not connected instead of throwing an exception.
        /// </summary>
        /// <param name="buffer">The byte array to write the input to.</param>
        /// <param name="offset">The offset in <paramref name="buffer"/> at which to write the bytes.</param>
        /// <param name="count">The maximum number of bytes to read. Fewer bytes are read if <paramref name="count"/> is greater than the number of bytes in the input buffer.</param>
        /// <returns>The number of bytes read.</returns>
        /// <exception cref="ArgumentNullException">The buffer passed is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The <paramref name="offset"/> or <paramref name="count"/> parameters are outside a valid region of the <paramref name="buffer"/> being passed. Either <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
        /// <exception cref="ArgumentException"><paramref name="offset"/> plus <paramref name="count"/> is greater than the length of the <paramref name="buffer"/>.</exception>
        public int TryRead(byte[] buffer, int offset, int count)
        {
            try
            {
                return Read(buffer, offset, count);
            }
            catch (InvalidOperationException ex) when (ex.Message == EX_PIPE_NOT_CONNECTED)
            {
                return 0;
            }
        }

        /// <summary>
        /// Synchronously reads one byte from the <see cref="NamedPipe"/> input buffer.
        /// </summary>
        /// <returns>The byte, cast to an <see cref="Int32"/>, or -1 if no byte is available in the buffer.</returns>
        /// <exception cref="InvalidOperationException">The specified pipe is not connected.</exception>
        public int ReadByte()
        {
            return SafeRead(() =>
            {
                ThrowIfPipeNotConnected();
            }, () =>
            {
                if (_readStart == _readEnd)
                {
                    return -1; // No data available
                }

                int byteRead = _inBuffer[_readStart];

                if (_readEnd == fullIndicatorConst)
                {
                    _readEnd = _readStart;
                }
                _readStart++;
                if (_readStart >= _inBuffer.Length)
                {
                    _readStart = 0; // rollover if needed
                }
                if (_readStart == _readEnd)
                {
                    _readStart = _readEnd = 0; // attempt to reset the buffer
                }

                return byteRead;
            });
        }

        /// <summary>
        /// Synchronously reads one byte from the <see cref="NamedPipe"/> input buffer.
        /// Returns -1 if the pipe is not connected instead of throwing an exception.
        /// </summary>
        /// <returns>The byte, cast to an <see cref="Int32"/>, or -1 if no byte is available in the buffer.</returns>
        public int TryReadByte()
        {
            try
            {
                return ReadByte();
            }
            catch (InvalidOperationException ex) when (ex.Message == EX_PIPE_NOT_CONNECTED)
            {
                return -1;
            }
        }

        // Core write method to support splitting into Write and TryWrite.
        private bool WriteCore(byte[] buffer, int offset, int count)
        {
            ThrowIfPipeNotConnected();
            ThrowIfInvalidArgument(buffer, offset, count);

            bool finiteTimeout = _writeTimeout != Timeout.Infinite;
            // Stopwatch to support resuming timeout when WriteAsync is canceled unintentionally
            var sw = finiteTimeout ? new Stopwatch() : null;

        Retry:
            try
            {
                int timeout = finiteTimeout
                    ? _writeTimeout - (int)sw.ElapsedMilliseconds
                    : _writeTimeout;
                if (timeout > 0 || !finiteTimeout) // not timed out yet
                {
                    if (finiteTimeout) sw.Start();
                    if (pipeStream.WriteAsync(buffer, offset, count).Wait(timeout))
                    {
                        return true; // return when Write succeeds
                    }
                    ExtensionMethods.CancelIoEx(pipeStream.SafePipeHandle); // cancel IO when timed out
                }

                throw new TimeoutException();
            }
            catch (AggregateException ae)
            {
                var ex = ae.InnerException;

                if (ex is TaskCanceledException)
                {
                    if (_readLoopRunning) // indicates whether the cancellation was because we called Disconnect()
                    {
                        // If the task was canceled while we were waiting and we did not call Disconnect(),
                        // it was because another thread called CancelIoEx, so canceling this Write was not
                        // intentional; therefore, we resume the operation.
                        if (finiteTimeout) sw.Stop();
                        goto Retry;
                    }
                    else
                    {
                        return false; // cancellation because we disconnected
                    }
                }
                else if (ex is NullReferenceException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is System.IO.IOException)
                {
                    // return if loses connection while writing
                    return false;
                }
                else
                {
                    throw ex;
                }
            }
        }

        /// <summary>
        /// Writes a specified number of bytes to the pipe using data from a buffer.
        /// </summary>
        /// <param name="buffer">The byte array that contains the data to write to the pipe.</param>
        /// <param name="offset">The zero-based byte offset in the <paramref name="buffer"/> parameter at which to begin copying bytes to the pipe.</param>
        /// <param name="count">The number of bytes to write.</param>
        /// <exception cref="InvalidOperationException">The specified pipe is not connected.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="buffer"/> passed is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The <paramref name="offset"/> or <paramref name="count"/> parameters are outside a valid region of the <paramref name="buffer"/> being passed. Either <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
        /// <exception cref="ArgumentException"><paramref name="offset"/> plus <paramref name="count"/> is greater than the length of the <paramref name="buffer"/>.</exception>
        /// <exception cref="TimeoutException">The operation did not complete before the time-out period ended.</exception>
        public void Write(byte[] buffer, int offset, int count)
            => WriteCore(buffer, offset, count); // Calls WriteCore and ignores the returned bool.

        /// <summary>
        /// Writes a specified number of bytes to the pipe using data from a buffer.
        /// Returns <c>false</c> if the pipe is not connected or the operation did not complete
        /// before timing out instead of throwing an exception.
        /// </summary>
        /// <returns><c>true</c> if the write operation succesfully returns; otherwise <c>false</c>.</returns>
        /// <param name="buffer">The byte array that contains the data to write to the pipe.</param>
        /// <param name="offset">The zero-based byte offset in the <paramref name="buffer"/> parameter at which to begin copying bytes to the pipe.</param>
        /// <param name="count">The number of bytes to write.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="buffer"/> passed is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The <paramref name="offset"/> or <paramref name="count"/> parameters are outside a valid region of the <paramref name="buffer"/> being passed. Either <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
        /// <exception cref="ArgumentException"><paramref name="offset"/> plus <paramref name="count"/> is greater than the length of the <paramref name="buffer"/>.</exception>
        public bool TryWrite(byte[] buffer, int offset, int count)
        {
            try
            {
                return WriteCore(buffer, offset, count);
            }
            catch (InvalidOperationException ex) when (ex.Message == EX_PIPE_NOT_CONNECTED)
            {
            }
            catch (TimeoutException)
            {
            }
            return false;
        }

        // ---------- Exceptions ---------- //
        protected const string EX_PIPE_NOT_CONNECTED = "Pipe is not connected.";

        protected void ThrowIfPipeNotConnected()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException(EX_PIPE_NOT_CONNECTED);
            }
        }

        protected void ThrowIfInvalidArgument(byte[] buffer, int offset, int count)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer), $"{nameof(buffer)} must not be null.");
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"{nameof(offset)} must be a non-negative integer.");
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"{nameof(count)} must be a non-negative integer.");
            }
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException($"Invalid {nameof(offset)} and {nameof(count)}.");
            }
        }
    }
}
