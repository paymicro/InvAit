using System.Net;
using System.Text.Json;

namespace UIBlazor.Tests.Helpers;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request);
    }

    public static MockHttpMessageHandler CreateJsonResponse<T>(T content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new MockHttpMessageHandler(req => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(content), System.Text.Encoding.UTF8, "application/json")
        }));
    }

    public static MockHttpMessageHandler CreateStringResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new MockHttpMessageHandler(req => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content)
        }));
    }
}
