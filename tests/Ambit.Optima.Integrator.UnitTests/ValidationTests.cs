using System.Globalization;
using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.SqlServer;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class EmployeeValidationTests
{
    private static EmployeeAddRequest Valid() =>
        new(Guid.NewGuid(), Guid.NewGuid(), " KOWJAN1 ", " Kowalski ", " Jan ");

    [Fact]
    public void Text_is_trimmed_and_missing_text_becomes_empty()
    {
        var employee = EmployeeValidation.Validate(Valid() with { PositionName = "  " });

        Assert.Equal("KOWJAN1", employee.EmployeeCode);
        Assert.Equal("Kowalski", employee.LastName);
        Assert.Equal("", employee.Pesel);
        Assert.Equal("", employee.Street);
        Assert.Null(employee.PositionName);
        Assert.Equal(EmployeeValidation.DefaultCenterId, employee.CenterId);
    }

    [Theory]
    [InlineData(null, "Kowalski", RejectionCodes.EmployeeNamesRequired)]
    [InlineData("KOWJAN1", " ", RejectionCodes.EmployeeNamesRequired)]
    public void Code_and_names_are_required(string? code, string lastName, int number)
    {
        var refused = Assert.Throws<EmployeeRejectedException>(() =>
            EmployeeValidation.Validate(Valid() with { EmployeeCode = code, LastName = lastName })
        );
        Assert.Equal(number, refused.Number);
    }

    [Fact]
    public void Ids_are_required()
    {
        var refused = Assert.Throws<EmployeeRejectedException>(() =>
            EmployeeValidation.Validate(Valid() with { EmployeeId = null })
        );
        Assert.Equal(RejectionCodes.EmployeeIdsRequired, refused.Number);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("1234567890a")]
    public void A_pesel_has_eleven_digits(string pesel)
    {
        var refused = Assert.Throws<EmployeeRejectedException>(() =>
            EmployeeValidation.Validate(Valid() with { Pesel = pesel })
        );
        Assert.Equal(RejectionCodes.PeselInvalid, refused.Number);
        Assert.DoesNotContain(pesel, refused.Message);
    }

    [Fact]
    public void An_unknown_document_kind_is_refused()
    {
        var refused = Assert.Throws<EmployeeRejectedException>(() =>
            EmployeeValidation.Validate(Valid() with { DocumentKind = "DriverLicence" })
        );
        Assert.Equal(RejectionCodes.DocumentKindUnknown, refused.Number);
    }

    [Theory]
    [InlineData("POL")]
    [InlineData("P1")]
    public void A_country_code_has_two_letters(string code)
    {
        var refused = Assert.Throws<EmployeeRejectedException>(() =>
            EmployeeValidation.Validate(Valid() with { CitizenshipCode = code })
        );
        Assert.Equal(RejectionCodes.CountryCodeInvalid, refused.Number);
    }

    [Fact]
    public void Every_field_too_long_for_Optima_is_named_and_nothing_is_cut()
    {
        var refused = Assert.Throws<EmployeeRejectedException>(() =>
            EmployeeValidation.Validate(
                Valid() with
                {
                    FirstName = new string('a', 31),
                    Email = new string('e', 128),
                    DocumentKind = "Passport",
                    DocumentNumber = "1234567890",
                }
            )
        );
        Assert.Equal(RejectionCodes.TooLongForOptima, refused.Number);
        Assert.Equal("Too long for Optima: firstName, email, documentNumber.", refused.Message);
    }

    [Fact]
    public void A_document_Optima_does_not_store_may_be_any_length()
    {
        var employee = EmployeeValidation.Validate(
            Valid() with
            {
                DocumentKind = "ResidenceCard",
                DocumentNumber = new string('1', 40),
            }
        );
        Assert.Equal(40, employee.DocumentNumber!.Length);
    }
}

public sealed class OptimaEmployeeValuesTests
{
    private static EmployeeAddRequest Valid(EmployeeAddRequest? overrides = null) =>
        EmployeeValidation.Validate(
            overrides
                ?? new EmployeeAddRequest(
                    Guid.Parse("b0000000-0000-4000-8000-00000000000a"),
                    Guid.Parse("b0000000-0000-4000-8000-00000000000b"),
                    "KOWJAN1",
                    "Kowalski",
                    "Jan"
                )
        );

    [Fact]
    public void The_import_id_is_upper_case_and_dates_are_compact()
    {
        var values = OptimaEmployeeValues.From(
            Valid() with
            {
                DateOfBirth = new DateOnly(1990, 5, 12),
                StartsOn = new DateOnly(2026, 11, 1),
            }
        );

        Assert.Equal("B0000000-0000-4000-8000-00000000000B", values.ImportRowId);
        Assert.Equal("19900512", values.DateOfBirth);
        Assert.Equal("20261101", values.StartsOn);
    }

    [Fact]
    public void Without_an_address_the_country_stays_Poland()
    {
        var values = OptimaEmployeeValues.From(Valid() with { AddressCountryCode = "UA" });

        Assert.Equal("PL", values.AddressCountryCode);
        Assert.Equal("Polska", values.AddressCountryName);
    }

    [Fact]
    public void Countries_become_Optima_names_and_an_unknown_one_an_empty_name()
    {
        var values = OptimaEmployeeValues.From(
            Valid() with
            {
                City = "Lwów",
                AddressCountryCode = "DE",
                CitizenshipCode = "UA",
            }
        );

        Assert.Equal("DE", values.AddressCountryCode);
        Assert.Equal("", values.AddressCountryName);
        Assert.Equal("UKRAIŃSKIE", values.CitizenshipName);
    }

    [Fact]
    public void Only_an_identity_card_or_a_passport_is_stored()
    {
        Assert.Equal(
            "ABC123456",
            OptimaEmployeeValues
                .From(Valid() with { DocumentKind = "IdentityCard", DocumentNumber = "ABC123456" })
                .IdentityCard
        );
        var residence = OptimaEmployeeValues.From(
            Valid() with
            {
                DocumentKind = "ResidenceCard",
                DocumentNumber = "RC1",
            }
        );
        Assert.Equal("", residence.IdentityCard);
        Assert.Equal("", residence.Passport);
    }

    [Fact]
    public void The_record_says_which_values_were_given_never_what_they_were()
    {
        var masked = OptimaEmployeeValues
            .From(Valid() with { Pesel = "90051212345", Email = "jan@example.com" })
            .MaskedParameters;

        Assert.Contains("\"has_pesel\":true", masked);
        Assert.Contains("\"has_email\":true", masked);
        Assert.DoesNotContain("90051212345", masked);
        Assert.DoesNotContain("jan@example.com", masked);
        Assert.DoesNotContain("Kowalski", masked);
    }
}

public sealed class DeductionValidationTests
{
    private static DeductionAddRequest Request(DeductionItem item, int month = 11) =>
        new(Guid.NewGuid(), " CDN_Firma ", 2026, month, item);

    private static DeductionItem Item() =>
        new(Guid.NewGuid(), Guid.NewGuid(), " KOWJAN1 ", 338, 500.00m);

    [Fact]
    public void A_valid_deduction_covers_its_whole_month()
    {
        var deduction = DeductionValidation.Validate(Request(Item(), month: 2));

        Assert.Equal("CDN_Firma", deduction.DatabaseName);
        Assert.Equal("KOWJAN1", deduction.EmployeeCode);
        Assert.Equal(new DateOnly(2026, 2, 1), deduction.ValidFrom);
        Assert.Equal(new DateOnly(2026, 2, 28), deduction.ValidTo);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.005")]
    [InlineData("10000000000000")]
    public void The_amount_is_positive_with_at_most_two_decimals(string amount)
    {
        var refused = Assert.Throws<DeductionRejectedException>(() =>
            DeductionValidation.Validate(
                Request(
                    Item() with
                    {
                        Amount = decimal.Parse(amount, CultureInfo.InvariantCulture),
                    }
                )
            )
        );
        Assert.Equal(RejectionCodes.DeductionAmountInvalid, refused.Number);
    }

    [Fact]
    public void Only_zloty_can_be_exported()
    {
        var refused = Assert.Throws<DeductionRejectedException>(() =>
            DeductionValidation.Validate(Request(Item() with { Currency = "EUR" }))
        );
        Assert.Equal(RejectionCodes.DeductionCurrencyNotSupported, refused.Number);
    }

    [Fact]
    public void The_month_must_exist()
    {
        var refused = Assert.Throws<DeductionRejectedException>(() =>
            DeductionValidation.Validate(Request(Item(), month: 13))
        );
        Assert.Equal(RejectionCodes.DeductionMonthInvalid, refused.Number);
    }

    [Fact]
    public void The_component_is_required()
    {
        var refused = Assert.Throws<DeductionRejectedException>(() =>
            DeductionValidation.Validate(Request(Item() with { OptimaId = null }))
        );
        Assert.Equal(RejectionCodes.DeductionTargetRequired, refused.Number);
    }
}

public sealed class OptimaMessagesTests
{
    [Fact]
    public void Only_OPT_codes_are_kept()
    {
        Assert.Equal(
            "OPT0012,OPT77A",
            OptimaMessages.Codes("Błędny PESEL 90051212345#OPT0012#OPT77A#opt1#OPT#OPT1 x")
        );
    }

    [Fact]
    public void A_message_without_codes_gives_nothing()
    {
        Assert.Null(OptimaMessages.Codes("Pracownik 90051212345 już istnieje"));
        Assert.Null(OptimaMessages.Codes((string?)null));
    }
}
