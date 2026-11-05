using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationService;

public interface INotice
{
    string? Oid { get; }
    string? Id { get; }
    bool Read { get; }
    DateTime CreatedAt { get; }
    string NotificationType { get; }
    string ToJson();
}

public abstract class Notice : INotice
{
    public string? Oid { get; init; }
    public string? Id { get; set; }
    public bool Read { get; set; }
    public DateTime CreatedAt { get; set; }
    public abstract string NotificationType { get; }
    public abstract bool HasValidData();

    public string ToJson() => JsonSerializer.Serialize(this, GetType(), NoticeParser.JsonOptions);
}

public abstract class Notice<TData> : Notice where TData : class
{
    public TData? Data { get; init; }
}

public sealed class DeclarationNotice : Notice<DeclarationData>
{
    public override string NotificationType => "DECLARATION";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class ArgumentBuilderNotice : Notice<ArgumentBuilderData>
{
    public override string NotificationType => "ARGUMENT_BUILDER";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class TimelineBuilderNotice : Notice<TimelineBuilderData>
{
    public override string NotificationType => "TIMELINE_BUILDER";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class DocumentIngestionNotice : Notice<IngestionData>
{
    public override string NotificationType => "INGESTION";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class DocumentEgestionNotice : Notice<DocumentData>
{
    public override string NotificationType => "EGESTION";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class CaseCreatedNotice : Notice<CaseData>
{
    public override string NotificationType => "CASE_CREATED";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class CaseDeletedNotice : Notice<CaseData>
{
    public override string NotificationType => "CASE_DELETED";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class AlertNotice : Notice<AlertData>
{
    public override string NotificationType => "ALERT";
    public override bool HasValidData() => Data?.IsValid() == true;
}

public sealed class ChatMessageNotice : Notice
{
    // Upstream deliberately leaves CHAT_MESSAGE data unconstrained.
    public JsonElement Data { get; init; }
    public override string NotificationType => "CHAT_MESSAGE";
    public override bool HasValidData() => Data.ValueKind != JsonValueKind.Undefined;
}

[JsonConverter(typeof(ExactEnumConverter<WorkflowStatus>))]
public enum WorkflowStatus { STARTED, SUCCESS, FAILED }

[JsonConverter(typeof(ExactEnumConverter<IngestionStatus>))]
public enum IngestionStatus { STARTED, SUCCESS, FAILED, PRELIMINARY, EVIDENCE_EXTRACTION }

[JsonConverter(typeof(ExactEnumConverter<ArgumentStatus>))]
public enum ArgumentStatus
{
    STARTED, INITIAL_ARGUMENT, COUNTER_ARGUMENT, FINAL_ARGUMENT,
    FACT_VALIDATION, REVISION, SUCCESS, FAILED
}

[JsonConverter(typeof(ExactEnumConverter<AlertStatus>))]
public enum AlertStatus { FEEDBACK_REQUIRED }

public sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<T>(reader.GetString(), ignoreCase: false, out var value) &&
            Enum.IsDefined(value) && reader.GetString() == value.ToString())
            return value;
        throw new JsonException($"Invalid {typeof(T).Name}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class OptionalStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString()! :
        throw new JsonException("Expected a string");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public abstract class AccountData
{
    public string? AccountId { get; init; }
    protected bool HasAccountId => !string.IsNullOrEmpty(AccountId);
}

public abstract class WorkflowData : AccountData
{
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? RequestText { get; init; }
    public string? SessionId { get; init; }
    public WorkflowStatus? Status { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? ErrorMessage { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? RequestId { get; init; }
    protected bool HasWorkflowFields => HasAccountId && !string.IsNullOrEmpty(SessionId) && Status.HasValue;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DeclarationData : WorkflowData
{
    public string? DeclarationUrl { get; init; }
    public bool IsValid() => HasWorkflowFields &&
        Uri.TryCreate(DeclarationUrl, UriKind.Absolute, out _);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineBuilderData : WorkflowData
{
    public bool IsValid() => HasWorkflowFields;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ArgumentBuilderData : AccountData
{
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? RequestText { get; init; }
    public string? SessionId { get; init; }
    public ArgumentStatus? Status { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? ErrorMessage { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? RequestId { get; init; }
    public bool IsValid() => HasAccountId && !string.IsNullOrEmpty(SessionId) && Status.HasValue;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DocumentData : AccountData
{
    public WorkflowStatus? Status { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? ErrorMessage { get; init; }
    public string? Filepath { get; init; }
    public bool IsValid() => HasAccountId && Status.HasValue && !string.IsNullOrEmpty(Filepath);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class IngestionData : AccountData
{
    public IngestionStatus? Status { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? ErrorMessage { get; init; }
    public string? Filepath { get; init; }
    public bool IsValid() => HasAccountId && Status.HasValue && !string.IsNullOrEmpty(Filepath);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CaseData : AccountData
{
    public WorkflowStatus? Status { get; init; }
    [JsonConverter(typeof(OptionalStringConverter))]
    public string? ErrorMessage { get; init; }
    public bool IsValid() => HasAccountId && Status.HasValue;
}

public readonly record struct SourceId(string? Text, long? Number)
{
    public bool IsValid => Text is { Length: > 0 } || Number >= 0;
}

public sealed class SourceIdConverter : JsonConverter<SourceId>
{
    public override SourceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new SourceId(reader.GetString(), null);
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
            return new SourceId(null, number);
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var floating) &&
            double.IsFinite(floating) && floating >= 0 && floating <= long.MaxValue &&
            Math.Truncate(floating) == floating)
            return new SourceId(null, (long)floating);
        throw new JsonException("Invalid sourceId");
    }

    public override void Write(Utf8JsonWriter writer, SourceId value, JsonSerializerOptions options)
    {
        if (value.Text is { } text) writer.WriteStringValue(text);
        else if (value.Number is { } number) writer.WriteNumberValue(number);
        else writer.WriteNullValue();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AlertData : AccountData
{
    [JsonConverter(typeof(SourceIdConverter))]
    public SourceId? SourceId { get; init; }
    public string? ClientFeedbackRequestId { get; init; }
    public string? MarkdownPath { get; init; }
    public AlertStatus? Status { get; init; }
    public bool IsValid() => HasAccountId && SourceId?.IsValid == true &&
        !string.IsNullOrEmpty(ClientFeedbackRequestId) && !string.IsNullOrEmpty(MarkdownPath) &&
        Status == AlertStatus.FEEDBACK_REQUIRED;
}

public static class NoticeParser
{
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool TryCreate(string json, out Notice? notice)
    {
        notice = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("notificationType", out var type) ||
                type.ValueKind != JsonValueKind.String)
                return false;

            notice = type.GetString() switch
            {
                "DECLARATION" => JsonSerializer.Deserialize<DeclarationNotice>(json, JsonOptions),
                "ARGUMENT_BUILDER" => JsonSerializer.Deserialize<ArgumentBuilderNotice>(json, JsonOptions),
                "TIMELINE_BUILDER" => JsonSerializer.Deserialize<TimelineBuilderNotice>(json, JsonOptions),
                "INGESTION" => JsonSerializer.Deserialize<DocumentIngestionNotice>(json, JsonOptions),
                "EGESTION" => JsonSerializer.Deserialize<DocumentEgestionNotice>(json, JsonOptions),
                "CASE_CREATED" => JsonSerializer.Deserialize<CaseCreatedNotice>(json, JsonOptions),
                "CASE_DELETED" => JsonSerializer.Deserialize<CaseDeletedNotice>(json, JsonOptions),
                "ALERT" => JsonSerializer.Deserialize<AlertNotice>(json, JsonOptions),
                "CHAT_MESSAGE" => JsonSerializer.Deserialize<ChatMessageNotice>(json, JsonOptions),
                _ => null
            };
            if (notice is null || string.IsNullOrEmpty(notice.Oid) ||
                notice.Id is { Length: > 0 and < 5 } || !notice.HasValidData())
                return false;

            notice.Id = string.IsNullOrEmpty(notice.Id) ? Guid.NewGuid().ToString() : notice.Id;
            notice.Read = false;
            notice.CreatedAt = DateTime.UtcNow;
            return true;
        }
        catch (JsonException)
        {
            notice = null;
            return false;
        }
    }
}
