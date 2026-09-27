using Newtonsoft.Json;

namespace Skua.Core.Models.Converters;

public class StringBoolConverter : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        writer.WriteValue(((bool)value) ? "1" : "0");
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        // "1"/"0", 1/0, or a JSON true/false (whose Value prints "True").
        return reader.Value switch
        {
            bool b => b,
            null => false,
            var v => v.ToString() is "1" or "true" or "True",
        };
    }

    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(bool);
    }
}