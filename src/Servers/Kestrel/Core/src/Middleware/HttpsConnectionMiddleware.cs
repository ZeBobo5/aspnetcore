// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.AspNetCore.Server.Kestrel.Https.Internal
{
    internal class HttpsConnectionMiddleware
    {
        private const string EnableWindows81Http2 = "Microsoft.AspNetCore.Server.Kestrel.EnableWindows81Http2";
        private readonly ConnectionDelegate _next;
        private readonly HttpsConnectionAdapterOptions _options;
        private readonly ILogger _logger;
        private readonly X509Certificate2 _serverCertificate;
        private readonly Func<ConnectionContext, string, X509Certificate2> _serverCertificateSelector;

        public HttpsConnectionMiddleware(ConnectionDelegate next, HttpsConnectionAdapterOptions options)
          : this(next, options, loggerFactory: NullLoggerFactory.Instance)
        {
        }

        public HttpsConnectionMiddleware(ConnectionDelegate next, HttpsConnectionAdapterOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;
            _logger = loggerFactory.CreateLogger<HttpsConnectionMiddleware>();

            // This configuration will always fail per-request, preemptively fail it here. See HttpConnection.SelectProtocol().
            if (options.HttpProtocols == HttpProtocols.Http2)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    throw new NotSupportedException(CoreStrings.Http2NoTlsOsx);
                }
                else if (IsWindowsVersionIncompatible())
                {
                    throw new NotSupportedException(CoreStrings.Http2NoTlsWin81);
                }
            }
            else if (options.HttpProtocols == HttpProtocols.Http1AndHttp2 && IsWindowsVersionIncompatible())
            {
                _logger.Http2DefaultCiphersInsufficient();
                options.HttpProtocols = HttpProtocols.Http1;
            }

            _next = next;
            // capture the certificate now so it can't be switched after validation
            _serverCertificate = options.ServerCertificate;
            _serverCertificateSelector = options.ServerCertificateSelector;
            if (_serverCertificate == null && _serverCertificateSelector == null)
            {
                throw new ArgumentException(CoreStrings.ServerCertificateRequired, nameof(options));
            }

            // If a selector is provided then ignore the cert, it may be a default cert.
            if (_serverCertificateSelector != null)
            {
                // SslStream doesn't allow both.
                _serverCertificate = null;
            }
            else
            {
                EnsureCertificateIsAllowedForServerAuth(_serverCertificate);
            }
        }

        public async Task OnConnectionAsync(ConnectionContext context)
        {
            await Task.Yield();

            bool certificateRequired;
            if (context.Features.Get<ITlsConnectionFeature>() != null)
            {
                await _next(context);
                return;
            }

            var feature = new Core.Internal.TlsConnectionFeature();
            context.Features.Set<ITlsConnectionFeature>(feature);
            context.Features.Set<ITlsHandshakeFeature>(feature);

            var memoryPool = context.Features.Get<IMemoryPoolFeature>()?.MemoryPool;

            var inputPipeOptions = new StreamPipeReaderOptions
            (
                pool: memoryPool,
                bufferSize: memoryPool.GetMinimumSegmentSize(),
                minimumReadSize: memoryPool.GetMinimumAllocSize(),
                leaveOpen: true
            );

            var outputPipeOptions = new StreamPipeWriterOptions
            (
                pool: memoryPool,
                leaveOpen: true
            );

            SslDuplexPipe sslDuplexPipe = null;

            if (_options.ClientCertificateMode == ClientCertificateMode.NoCertificate)
            {
                sslDuplexPipe = new SslDuplexPipe(context.Transport, inputPipeOptions, outputPipeOptions);
                certificateRequired = false;
            }
            else
            {
                sslDuplexPipe = new SslDuplexPipe(context.Transport, inputPipeOptions, outputPipeOptions, s => new SslStream(s,
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate == null)
                        {
                            return _options.ClientCertificateMode != ClientCertificateMode.RequireCertificate;
                        }

                        if (_options.ClientCertificateValidation == null)
                        {
                            if (sslPolicyErrors != SslPolicyErrors.None)
                            {
                                return false;
                            }
                        }

                        var certificate2 = ConvertToX509Certificate2(certificate);
                        if (certificate2 == null)
                        {
                            return false;
                        }

                        if (_options.ClientCertificateValidation != null)
                        {
                            if (!_options.ClientCertificateValidation(certificate2, chain, sslPolicyErrors))
                            {
                                return false;
                            }
                        }

                        return true;
                    }));

                certificateRequired = true;
            }

            var sslStream = sslDuplexPipe.Stream;

            using (var cancellationTokeSource = new CancellationTokenSource(_options.HandshakeTimeout))
            {
                try
                {
                    // Adapt to the SslStream signature
                    ServerCertificateSelectionCallback selector = null;
                    if (_serverCertificateSelector != null)
                    {
                        selector = (sender, name) =>
                        {
                            feature.HostName = name;
                            context.Features.Set(sslStream);
                            var cert = _serverCertificateSelector(context, name);
                            if (cert != null)
                            {
                                EnsureCertificateIsAllowedForServerAuth(cert);
                            }
                            return cert;
                        };
                    }

                    var sslOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _serverCertificate,
                        ServerCertificateSelectionCallback = selector,
                        ClientCertificateRequired = certificateRequired,
                        EnabledSslProtocols = _options.SslProtocols,
                        CertificateRevocationCheckMode = _options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                        ApplicationProtocols = new List<SslApplicationProtocol>()
                    };

                    // This is order sensitive
                    if ((_options.HttpProtocols & HttpProtocols.Http2) != 0)
                    {
                        sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http2);
                        // https://tools.ietf.org/html/rfc7540#section-9.2.1
                        sslOptions.AllowRenegotiation = false;
                    }

                    if ((_options.HttpProtocols & HttpProtocols.Http1) != 0)
                    {
                        sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http11);
                    }

                    _options.OnAuthenticate?.Invoke(context, sslOptions);

                    KestrelEventSource.Log.TlsHandshakeStart(context, sslOptions);

                    await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationTokeSource.Token);
                }
                catch (OperationCanceledException)
                {
                    KestrelEventSource.Log.TlsHandshakeFailed(context.ConnectionId);
                    KestrelEventSource.Log.TlsHandshakeStop(context, null);

                    _logger.AuthenticationTimedOut();
                    await sslStream.DisposeAsync();
                    return;
                }
                catch (IOException ex)
                {
                    KestrelEventSource.Log.TlsHandshakeFailed(context.ConnectionId);
                    KestrelEventSource.Log.TlsHandshakeStop(context, null);

                    _logger.AuthenticationFailed(ex);
                    await sslStream.DisposeAsync();
                    return;
                }
                catch (AuthenticationException ex)
                {
                    KestrelEventSource.Log.TlsHandshakeFailed(context.ConnectionId);
                    KestrelEventSource.Log.TlsHandshakeStop(context, null);

                    _logger.AuthenticationFailed(ex);

                    await sslStream.DisposeAsync();
                    return;
                }
            }

            feature.ApplicationProtocol = sslStream.NegotiatedApplicationProtocol.Protocol;
            context.Features.Set<ITlsApplicationProtocolFeature>(feature);
            feature.ClientCertificate = ConvertToX509Certificate2(sslStream.RemoteCertificate);
            feature.CipherAlgorithm = sslStream.CipherAlgorithm;
            feature.CipherStrength = sslStream.CipherStrength;
            feature.HashAlgorithm = sslStream.HashAlgorithm;
            feature.HashStrength = sslStream.HashStrength;
            feature.KeyExchangeAlgorithm = sslStream.KeyExchangeAlgorithm;
            feature.KeyExchangeStrength = sslStream.KeyExchangeStrength;
            feature.Protocol = sslStream.SslProtocol;

            KestrelEventSource.Log.TlsHandshakeStop(context, feature);

            _logger.HttpsConnectionEstablished(context.ConnectionId, sslStream.SslProtocol);

            var originalTransport = context.Transport;

            try
            {
                context.Transport = sslDuplexPipe;

                // Disposing the stream will dispose the sslDuplexPipe
                await using (sslStream)
                await using (sslDuplexPipe)
                {
                    await _next(context);
                    // Dispose the inner stream (SslDuplexPipe) before disposing the SslStream
                    // as the duplex pipe can hit an ODE as it still may be writing.
                }
            }
            finally
            {
                // Restore the original so that it gets closed appropriately
                context.Transport = originalTransport;
            }
        }

        private static void EnsureCertificateIsAllowedForServerAuth(X509Certificate2 certificate)
        {
            if (!CertificateLoader.IsCertificateAllowedForServerAuth(certificate))
            {
                throw new InvalidOperationException(CoreStrings.FormatInvalidServerCertificateEku(certificate.Thumbprint));
            }
        }

        private static X509Certificate2 ConvertToX509Certificate2(X509Certificate certificate)
        {
            if (certificate == null)
            {
                return null;
            }

            if (certificate is X509Certificate2 cert2)
            {
                return cert2;
            }

            return new X509Certificate2(certificate);
        }

        private static bool IsWindowsVersionIncompatible()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var enableHttp2OnWindows81 = AppContext.TryGetSwitch(EnableWindows81Http2, out var enabled) && enabled;
                if (Environment.OSVersion.Version < new Version(6, 3) // Missing ALPN support
                    // Win8.1 and 2012 R2 don't support the right cipher configuration by default.
                    || (Environment.OSVersion.Version < new Version(10, 0) && !enableHttp2OnWindows81))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class HttpsConnectionMiddlewareLoggerExtensions
    {

        private static readonly Action<ILogger, Exception> _authenticationFailed =
            LoggerMessage.Define(
                logLevel: LogLevel.Debug,
                eventId: new EventId(1, "AuthenticationFailed"),
                formatString: CoreStrings.AuthenticationFailed);

        private static readonly Action<ILogger, Exception> _authenticationTimedOut =
            LoggerMessage.Define(
                logLevel: LogLevel.Debug,
                eventId: new EventId(2, "AuthenticationTimedOut"),
                formatString: CoreStrings.AuthenticationTimedOut);

        private static readonly Action<ILogger, string, SslProtocols, Exception> _httpsConnectionEstablished =
            LoggerMessage.Define<string, SslProtocols>(
                logLevel: LogLevel.Debug,
                eventId: new EventId(3, "HttpsConnectionEstablished"),
                formatString: CoreStrings.HttpsConnectionEstablished);

        private static readonly Action<ILogger, Exception> _http2DefaultCiphersInsufficient =
            LoggerMessage.Define(
                logLevel: LogLevel.Information,
                eventId: new EventId(4, "Http2DefaultCiphersInsufficient"),
                formatString: CoreStrings.Http2DefaultCiphersInsufficient);

        public static void AuthenticationFailed(this ILogger logger, Exception exception) => _authenticationFailed(logger, exception);

        public static void AuthenticationTimedOut(this ILogger logger) => _authenticationTimedOut(logger, null);

        public static void HttpsConnectionEstablished(this ILogger logger, string connectionId, SslProtocols sslProtocol) => _httpsConnectionEstablished(logger, connectionId, sslProtocol, null);

        public static void Http2DefaultCiphersInsufficient(this ILogger logger) => _http2DefaultCiphersInsufficient(logger, null);
    }
}
