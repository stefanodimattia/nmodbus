using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using log4net;
using Modbus.Message;
using Unme.Common.NullReferenceExtension;
using Unme.Common;

namespace Modbus.IO
{
    /// <summary>
    /// Modbus transport.
    /// Abstraction - http://en.wikipedia.org/wiki/Bridge_Pattern
    /// </summary>
    public abstract class ModbusTransport : IDisposable
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ModbusTransport));
        private object _syncLock = new object();
        private int _retries = Modbus.DefaultRetries;
        private int _waitToRetryMilliseconds = Modbus.DefaultWaitToRetryMilliseconds;
        private IStreamResource _streamResource;

        /// <summary>
        /// This constructor is called by the NullTransport.
        /// </summary>
        internal ModbusTransport()
        {
        }

        internal ModbusTransport(IStreamResource streamResource)
        {
            Debug.Assert(streamResource != null, "Argument streamResource cannot be null.");

            _streamResource = streamResource;
        }

        /// <summary>
        /// Number of times to retry sending message after encountering a failure such as an IOException, 
        /// TimeoutException, or a corrupt message.
        /// </summary>
        public int Retries
        {
            get { return _retries; }
            set { _retries = value; }
        }

        /// <summary>
        /// Gets or sets the number of milliseconds the tranport will wait before retrying a message after receiving 
        /// an ACKNOWLEGE or SLAVE DEVICE BUSY slave exception response.
        /// </summary>
        public int WaitToRetryMilliseconds
        {
            get
            {
                return _waitToRetryMilliseconds;
            }
            set
            {
                if (value < 0)
                    throw new ArgumentException("WaitToRetryMilliseconds must be greater than 0.");

                _waitToRetryMilliseconds = value;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets or sets the stream resource.
        /// </summary>
        internal IStreamResource StreamResource
        {
            get
            {
                return _streamResource;
            }
        }

        internal virtual T UnicastMessage<T>(IModbusMessage message) where T : IModbusMessage, new()
        {
            IModbusMessage response = null;
            int attempt = 1;
            bool readAgain;
            bool success = false;

            do
            {
                try
                {
                    lock (_syncLock)
                    {
                        Write(message);

                        do
                        {
                            readAgain = false;
                            response = ReadResponse<T>();

                            var exceptionResponse = response as SlaveExceptionResponse;
                            if (exceptionResponse != null)
                            {
                                // if SlaveExceptionCode == ACKNOWLEDGE we retry reading the response without resubmitting request
                                if (readAgain = exceptionResponse.SlaveExceptionCode == Modbus.Acknowledge)
                                {
                                    _logger.InfoFormat("Received ACKNOWLEDGE slave exception response, waiting {0} milliseconds and retrying to read response.", _waitToRetryMilliseconds);
                                    Thread.Sleep(WaitToRetryMilliseconds);
                                }
                                else
                                {
                                    throw new SlaveException(exceptionResponse);
                                }
                            }

                        } while (readAgain);
                    }

                    ValidateResponse(message, response);
                    success = true;
                }
                catch (SlaveException se)
                {
                    if (se.SlaveExceptionCode != Modbus.SlaveDeviceBusy)
                        throw;

                    _logger.InfoFormat("Received SLAVE_DEVICE_BUSY exception response, waiting {0} milliseconds and resubmitting request.", _waitToRetryMilliseconds);
                    Thread.Sleep(WaitToRetryMilliseconds);
                }
                catch (Exception e)
                {
                    if (e is FormatException ||
                        e is NotImplementedException ||
                        e is TimeoutException ||
                        e is IOException)
                    {
                        _logger.WarnFormat("{0}, {1} retries remaining - {2}", e.GetType().Name, _retries - attempt, e);

                        if (attempt++ > _retries)
                            throw;
                    }
                    else
                    {
                        throw;
                    }
                }

            } while (!success);

            return (T) response;
        }

        internal virtual IModbusMessage CreateResponse<T>(byte[] frame) where T : IModbusMessage, new()
        {
            byte functionCode = frame[1];
            IModbusMessage response;

            // check for slave exception response
            if (functionCode > Modbus.ExceptionOffset)
                response = ModbusMessageFactory.CreateModbusMessage<SlaveExceptionResponse>(frame);
            else
                // create message from frame
                response = ModbusMessageFactory.CreateModbusMessage<T>(frame);

            return response;
        }

        internal void ValidateResponse(IModbusMessage request, IModbusMessage response)
        {
            // always check the function code and slave address, regardless of transport protocol
            if (request.FunctionCode != response.FunctionCode)
                throw new IOException(String.Format(CultureInfo.InvariantCulture, "Received response with unexpected Function Code. Expected {0}, received {1}.", request.FunctionCode, response.FunctionCode));

            if (request.SlaveAddress != response.SlaveAddress)
                throw new IOException(String.Format(CultureInfo.InvariantCulture, "Response slave address does not match request. Expected {0}, received {1}.", response.SlaveAddress, request.SlaveAddress));

            // message specific validation
            request.Is<IModbusRequest>(req => req.ValidateResponse(response));

            OnValidateResponse(request, response);
        }

        /// <summary>
        /// Provide hook to do transport level message validation.
        /// </summary>
        internal abstract void OnValidateResponse(IModbusMessage request, IModbusMessage response);

        internal abstract byte[] ReadRequest();

        internal abstract IModbusMessage ReadResponse<T>() where T : IModbusMessage, new();

        internal abstract byte[] BuildMessageFrame(IModbusMessage message);

        internal abstract void Write(IModbusMessage message);

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                DisposableUtility.Dispose(ref _streamResource);
        }
    }
}
