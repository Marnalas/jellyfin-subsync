using System.Net;
using System.Text;

namespace Jellyfin.Subsync.Starter.Tests.TestDoubles
{
    /// <summary>One HTTP call the code under test made.</summary>
    internal sealed record RecordedRequest(HttpMethod Method, string Url, string? Body)
    {
        public string Path => new Uri(Url).AbsolutePath;
    }

    /// <summary>
    /// A scripted transport. Hand-written rather than pulled in with a mocking
    /// library, in keeping with the rest of this suite - and because what these
    /// tests actually need is a request log, which is barely more than a list.
    /// </summary>
    internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            int ordinal;
            lock (_gate)
            {
                _requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body));
                ordinal = _requests.Count - 1;
            }

            return respond(request, ordinal);
        }

        public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
            new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Status(HttpStatusCode statusCode) => new(statusCode);
    }

    /// <summary>
    /// Hands out a new HttpClient per call, like the real factory.
    /// <see cref="CreateCount"/> is what pins the fix for the singleton
    /// HttpClient: the client must ask for one per HTTP call so the factory can
    /// rotate handlers, not cache one for the life of the process.
    /// </summary>
    internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _createCount);
            // disposeHandler: false - the client disposes each HttpClient it
            // makes, and the handler has to outlive them to keep the request log.
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
