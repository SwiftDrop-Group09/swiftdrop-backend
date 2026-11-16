using System.Text.Json.Serialization;

namespace SwiftDrop.Delivery.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VehicleType
{
    Bicycle = 1,
    Motorcycle = 2,
    Car = 3,
    Van = 4,
    Truck = 5
}

