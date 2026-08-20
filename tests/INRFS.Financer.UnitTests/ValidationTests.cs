using FluentValidation.TestHelper;
using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Xunit;

namespace INRFS.Financer.UnitTests;

public sealed class ValidationTests
{
    [Fact]
    public void Login_rejects_invalid_portal_and_email()
    {
        var result = new LoginValidator().TestValidate(
            new LoginRequest("invalid", "password", "customer")
        );
        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.Portal);
    }

    [Fact]
    public void Login_accepts_a_mobile_number_for_the_financer_portal()
    {
        var result = new LoginValidator().TestValidate(
            new LoginRequest("+919876543210", "StrongPassword1", "financer")
        );
        result.ShouldNotHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Registration_requires_contact_and_business_details_without_a_password()
    {
        var result = new RegisterFinancerValidator().TestValidate(
            new RegisterFinancerRequest(
                "Suresh Patel",
                "Patel Finance",
                "+919876543210",
                "suresh@example.com",
                "Ahmedabad",
                "Gujarat"
            )
        );
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Customer_rejects_minor_and_invalid_pan()
    {
        var request = new CreateCustomerRequest(
            null,
            "A Customer",
            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-12)),
            null,
            "+919876543210",
            "a@example.com",
            "Address",
            null,
            "City",
            "State",
            "123456",
            "123456789012",
            "bad"
        );
        var result = new CustomerValidator().TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.DateOfBirth);
        result.ShouldHaveValidationErrorFor(x => x.Pan);
    }

    [Fact]
    public void Product_rejects_inverted_limits()
    {
        var request = new LoanProductRequest(
            "X",
            "Test",
            10000,
            1000,
            12,
            6,
            18,
            InterestMethod.ReducingBalance,
            RepaymentFrequency.Monthly,
            1,
            2,
            18,
            70,
            50
        );
        var result = new LoanProductValidator().TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.MaximumPrincipal);
        result.ShouldHaveValidationErrorFor(x => x.MaximumTenureMonths);
    }

    [Fact]
    public void Payment_requires_positive_amount()
    {
        var result = new PaymentValidator().TestValidate(
            new RecordPaymentRequest(
                Guid.NewGuid(),
                null,
                0,
                DateTimeOffset.UtcNow,
                PaymentMode.Cash,
                null,
                null
            )
        );
        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }
}
