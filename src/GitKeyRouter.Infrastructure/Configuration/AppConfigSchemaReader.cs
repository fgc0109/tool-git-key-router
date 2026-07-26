using System.Text.Json;

namespace GitKeyRouter.Infrastructure.Configuration;

internal static class AppConfigSchemaReader
{
    public static int Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The application configuration must be a JSON object.");
        }

        int? schemaVersion = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (schemaVersion is not null)
            {
                throw new JsonException("The application configuration contains duplicate schema-version properties.");
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
            {
                throw new JsonException("The application configuration schema version must be an integer.");
            }

            schemaVersion = value;
        }

        return schemaVersion ?? 1;
    }

    public static int? TryRead(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
