// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================

using System;
using System.IO;
using System.Net;
using System.Threading;

// Offline stand-in for the updater's download.
//
// update.exe (net472) picks up its WebRequest creator from
// <system.net><webRequestModules>, so a config entry can point the https prefix
// at this factory and the product's own download path runs end to end without a
// socket.  The payload is read at request time from V250_UPDATE_PAYLOAD, which
// lets the harness stage a different archive per scenario.
//
// This is the .NET Framework counterpart of DOTNET_STARTUP_HOOKS used for
// AutoManager: the network is stubbed, the product logic is not.

namespace UpdateProbe
{
    public sealed class StubFactory : IWebRequestCreate
    {
        static StubFactory()
        {
            Log("factory type loaded");
        }

        public WebRequest Create(Uri uri)
        {
            Log("Create " + uri);
            string payload = Environment.GetEnvironmentVariable("V250_UPDATE_PAYLOAD");
            if (string.IsNullOrEmpty(payload) || !File.Exists(payload))
            {
                Log("payload missing: " + (payload ?? "(null)"));
                throw new WebException("V250_UPDATE_PAYLOAD is not a readable file");
            }
            byte[] body = File.ReadAllBytes(payload);
            Log("serving " + body.Length + " bytes");
            return new StubRequest(uri, body);
        }

        internal static void Log(string message)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("V250_UPDATE_LOG");
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + message + "\r\n");
            }
            catch
            {
            }
        }
    }

    internal sealed class StubRequest : WebRequest
    {
        private readonly Uri _uri;        private readonly byte[] _body;
        private WebHeaderCollection _headers = new WebHeaderCollection();
        private string _method = "GET";
        private int _timeout = 100000;
        private long _contentLength = -1;

        internal StubRequest(Uri uri, byte[] body)
        {
            _uri = uri;
            _body = body;
            _contentLength = body.Length;
        }

        public override Uri RequestUri { get { return _uri; } }
        public override string Method { get { return _method; } set { _method = value; } }
        public override int Timeout { get { return _timeout; } set { _timeout = value; } }

        public override WebHeaderCollection Headers
        {
            get { return _headers; }
            set { _headers = value ?? new WebHeaderCollection(); }
        }

        public override long ContentLength { get { return _contentLength; } set { _contentLength = value; } }
        public override string ContentType { get { return "application/zip"; } set { } }
        public override ICredentials Credentials { get { return null; } set { } }
        public override bool PreAuthenticate { get { return false; } set { } }

        public override Stream GetRequestStream()
        {
            throw new NotSupportedException("stub request has no body");
        }

        public override WebResponse GetResponse()
        {
            return new StubResponse(_uri, _body);
        }

        public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state)
        {
            var result = new CompletedAsyncResult(state, GetResponse());
            if (callback != null)
            {
                ThreadPool.QueueUserWorkItem(delegate { callback(result); });
            }
            return result;
        }

        public override WebResponse EndGetResponse(IAsyncResult asyncResult)
        {
            var result = asyncResult as CompletedAsyncResult;
            if (result == null)
            {
                throw new ArgumentException("not a stub async result", "asyncResult");
            }
            return result.Response;
        }

        public override void Abort()
        {
        }
    }

    internal sealed class CompletedAsyncResult : IAsyncResult
    {
        private readonly object _state;
        private readonly WebResponse _response;
        private readonly ManualResetEvent _done = new ManualResetEvent(true);

        internal CompletedAsyncResult(object state, WebResponse response)
        {
            _state = state;
            _response = response;
        }

        internal WebResponse Response { get { return _response; } }
        public object AsyncState { get { return _state; } }
        public WaitHandle AsyncWaitHandle { get { return _done; } }
        public bool CompletedSynchronously { get { return true; } }
        public bool IsCompleted { get { return true; } }
    }

    internal sealed class StubResponse : WebResponse
    {
        private readonly Uri _uri;
        private readonly byte[] _body;
        private readonly WebHeaderCollection _headers = new WebHeaderCollection();

        internal StubResponse(Uri uri, byte[] body)
        {
            _uri = uri;
            _body = body;
            _headers["Content-Length"] = body.Length.ToString();
            _headers["Content-Type"] = "application/zip";
        }

        public override Uri ResponseUri { get { return _uri; } }
        public override long ContentLength { get { return _body.Length; } }
        public override string ContentType { get { return "application/zip"; } }
        public override WebHeaderCollection Headers { get { return _headers; } }

        public override Stream GetResponseStream()
        {
            return new MemoryStream(_body, false);
        }
    }
}

// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================
