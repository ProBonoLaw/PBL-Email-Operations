namespace NotificationService;

public static class NoticeEndpoints
{
    public static async Task<IResult> SendNotice(HttpRequest request, NoticeService service,
        ILogger<NoticeService> logger, CancellationToken token)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(token);
        if (!NoticeParser.TryCreate(body, out var notice))
            return Results.BadRequest("Invalid request payload.");

        try
        {
            await service.SendAsync(notice!, token);
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Sending notice failed");
            return Results.Problem("Internal server error.", statusCode: 500);
        }
    }
}
