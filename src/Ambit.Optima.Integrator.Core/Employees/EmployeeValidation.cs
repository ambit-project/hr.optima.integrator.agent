namespace Ambit.Optima.Integrator.Core.Employees;

/// <summary>
/// Checks an employee before any company is touched, and returns it trimmed, with an empty string for every missing
/// text except the position (<c>null</c> there means "do not look one up"). Nothing is truncated: a value longer than
/// Optima's column refuses the request and names every such field.
/// </summary>
public static class EmployeeValidation
{
    public const int DefaultCenterId = 3;

    private static readonly IReadOnlySet<string> DocumentKinds = new HashSet<string>(
        ["IdentityCard", "Passport", "ResidenceCard", "Other"],
        StringComparer.Ordinal
    );

    public static EmployeeAddRequest Validate(EmployeeAddRequest request)
    {
        var employee = request with
        {
            EmployeeCode = Clean(request.EmployeeCode),
            LastName = Clean(request.LastName),
            FirstName = Clean(request.FirstName),
            DatabaseName = Optional(request.DatabaseName),
            CenterId = request.CenterId ?? DefaultCenterId,
            Pesel = Clean(request.Pesel),
            CitizenshipCode = Optional(request.CitizenshipCode)?.ToUpperInvariant(),
            Email = Clean(request.Email),
            Phone = Clean(request.Phone),
            Street = Clean(request.Street),
            BuildingNumber = Clean(request.BuildingNumber),
            UnitNumber = Clean(request.UnitNumber),
            PostalCode = Clean(request.PostalCode),
            City = Clean(request.City),
            AddressCountryCode = Optional(request.AddressCountryCode)?.ToUpperInvariant(),
            Note = Clean(request.Note),
            DocumentKind = Optional(request.DocumentKind),
            DocumentNumber = Clean(request.DocumentNumber),
            PositionName = Optional(request.PositionName),
        };

        if (employee.OperationId is null || employee.EmployeeId is null)
            throw Rejected(
                RejectionCodes.EmployeeIdsRequired,
                "operationId and employeeId are required."
            );
        if (employee.EmployeeCode == "" || employee.LastName == "" || employee.FirstName == "")
            throw Rejected(
                RejectionCodes.EmployeeNamesRequired,
                "employeeCode, lastName and firstName are required."
            );
        if (
            employee.Pesel != ""
            && (employee.Pesel!.Length != 11 || !employee.Pesel.All(char.IsAsciiDigit))
        )
            throw Rejected(RejectionCodes.PeselInvalid, "pesel must have 11 digits.");
        if (employee.DocumentKind is { } kind && !DocumentKinds.Contains(kind))
            throw Rejected(RejectionCodes.DocumentKindUnknown, "documentKind is not known.");
        if (!IsCountryCode(employee.CitizenshipCode) || !IsCountryCode(employee.AddressCountryCode))
            throw Rejected(
                RejectionCodes.CountryCodeInvalid,
                "A country code must have two letters."
            );

        var tooLong = Limits(employee)
            .Where(field => field.Value.Length > field.MaxLength)
            .Select(field => field.Name)
            .ToList();
        if (tooLong.Count > 0)
            throw Rejected(
                RejectionCodes.TooLongForOptima,
                $"Too long for Optima: {string.Join(", ", tooLong)}."
            );

        return employee;
    }

    /// <summary>Each limit is the size of Optima's column.</summary>
    private static IEnumerable<(string Name, string Value, int MaxLength)> Limits(
        EmployeeAddRequest employee
    )
    {
        yield return ("employeeCode", employee.EmployeeCode!, 20);
        yield return ("lastName", employee.LastName!, 40);
        yield return ("firstName", employee.FirstName!, 30);
        yield return ("email", employee.Email!, 127);
        yield return ("phone", employee.Phone!, 20);
        yield return ("street", employee.Street!, 40);
        yield return ("buildingNumber", employee.BuildingNumber!, 10);
        yield return ("unitNumber", employee.UnitNumber!, 10);
        yield return ("postalCode", employee.PostalCode!, 10);
        yield return ("city", employee.City!, 40);
        yield return ("note", employee.Note!, 1024);
        // Only an identity card or a passport is stored; another kind becomes a warning, so its length does not matter.
        if (employee.DocumentKind is "Passport")
            yield return ("documentNumber", employee.DocumentNumber!, 9);
        else if (employee.DocumentKind is "IdentityCard")
            yield return ("documentNumber", employee.DocumentNumber!, 12);
    }

    private static bool IsCountryCode(string? code) =>
        code is null || (code.Length == 2 && code.All(char.IsAsciiLetter));

    private static string Clean(string? value) => value?.Trim() ?? "";

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static EmployeeRejectedException Rejected(int number, string message) =>
        new(number, message);
}
