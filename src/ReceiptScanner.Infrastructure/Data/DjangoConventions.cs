using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ReceiptScanner.Infrastructure.Data;

/// <summary>How Django stores things in MySQL, expressed for EF Core.</summary>
public static class DjangoConventions
{
    /// <summary>
    /// Django's UUIDField on MySQL is <c>char(32)</c>: 32 lowercase hex digits,
    /// no dashes. EF's default would be char(36) with dashes and would never
    /// match an existing row.
    /// </summary>
    public static readonly ValueConverter<Guid, string> DjangoUuid = new(
        g => g.ToString("N"),
        s => Guid.ParseExact(s, "N"));

    /// <summary>
    /// Django (USE_TZ=True) writes UTC into datetime(6) columns, which come back
    /// with Kind=Unspecified. Stamp them as UTC on read, and normalise to UTC
    /// on write, so serialising with a trailing Z is honest.
    /// </summary>
    public static readonly ValueConverter<DateTime, DateTime> UtcDateTime = new(
        v => v.Kind == DateTimeKind.Utc ? v
            : v.Kind == DateTimeKind.Local ? v.ToUniversalTime()
            : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    /// <summary>
    /// DateOnly ↔ DateTime for <c>date</c> columns. Oracle's MySql.Data can
    /// write a DateOnly but only reads a date column back as DateTime, so EF's
    /// native DateOnly mapping fails on read ("Unable to cast DateTime to
    /// DateOnly"). Converting at the edge keeps DateOnly in the model.
    /// </summary>
    public static readonly ValueConverter<DateOnly, DateTime> DateOnlyAsDateTime = new(
        d => d.ToDateTime(TimeOnly.MinValue),
        dt => DateOnly.FromDateTime(dt));

    public static PropertyBuilder<Guid?> IsDjangoUuid(this PropertyBuilder<Guid?> property) =>
        property.HasConversion(DjangoUuid).HasColumnType("char(32)").HasMaxLength(32);

    /// <summary>
    /// Apply the conventions every Django column follows. Call at the end of
    /// OnModelCreating, after the explicit configuration.
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                // snake_case column names (ClientUuid → client_uuid,
                // UploadedById → uploaded_by_id), unless set explicitly.
                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName) is null)
                    property.SetColumnName(ToSnakeCase(property.Name));

                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr == typeof(bool))
                    property.SetColumnType("tinyint(1)");   // Django BooleanField on MySQL
                if (clr == typeof(DateOnly) && property.GetValueConverter() is null)
                {
                    property.SetValueConverter(DateOnlyAsDateTime);
                    property.SetColumnType("date");   // Django DateField
                }

                if (clr == typeof(DateTime) && property.GetValueConverter() is null)
                {
                    property.SetValueConverter(UtcDateTime);
                    // Django DateTimeField on MySQL is datetime(6).
                    property.SetColumnType("datetime(6)");
                }
            }
        }
    }

    public static string ToSnakeCase(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
