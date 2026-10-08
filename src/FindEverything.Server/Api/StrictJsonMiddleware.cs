using System.Text.Json;

namespace FindEverything.Server.Api;

// System.Text.Json rejects unknown properties during model binding; this also rejects duplicates.
public sealed class StrictJsonMiddleware(RequestDelegate next)
{
    public const int MaximumBodyBytes = 512 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.HasJsonContentType() && context.Request.Method is "POST" or "PUT" or "PATCH")
        {
            if (context.Request.ContentLength > MaximumBodyBytes)
                throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
            context.Request.EnableBuffering(bufferThreshold: MaximumBodyBytes, bufferLimit: MaximumBodyBytes);
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body,
                    new JsonDocumentOptions { MaxDepth = 64 }, context.RequestAborted);
                Check(document.RootElement);
            }
            catch (IOException) { throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge); }
            finally { context.Request.Body.Position = 0; }
        }
        await next(context);
    }

    private static void Check(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON properties are not allowed.");
                Check(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Check(child);
    }
}
