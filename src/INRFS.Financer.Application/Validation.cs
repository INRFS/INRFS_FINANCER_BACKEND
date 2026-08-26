using FluentValidation;

namespace INRFS.Financer.Application;

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .Must(value =>
                !string.IsNullOrWhiteSpace(value)
                && (
                    System.Net.Mail.MailAddress.TryCreate(value, out _)
                    || System.Text.RegularExpressions.Regex.IsMatch(value, "^[+0-9 ()-]{8,24}$")
                )
            )
            .WithMessage("Enter a valid email address or mobile number.");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Portal).Must(x => x is "admin" or "financer");
    }
}

public sealed class RegisterFinancerValidator : AbstractValidator<RegisterFinancerRequest>
{
    public RegisterFinancerValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MinimumLength(2).MaximumLength(100)
            .Matches("^[^0-9]+$").WithMessage("Full name must not contain numbers.");
        RuleFor(x => x.BusinessName).NotEmpty().MinimumLength(2).MaximumLength(200);
        RuleFor(x => x.Mobile).Must(value =>
            System.Text.RegularExpressions.Regex.IsMatch(
                System.Text.RegularExpressions.Regex.Replace(value ?? string.Empty, "[^0-9]", string.Empty),
                "^(91)?[6-9][0-9]{9}$"))
            .WithMessage("Enter a valid 10-digit Indian mobile number.");
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(x => x.City).NotEmpty().MinimumLength(2).MaximumLength(100)
            .Matches("^[^0-9]+$").WithMessage("City must not contain numbers.");
        RuleFor(x => x.State).NotEmpty().MinimumLength(2).MaximumLength(100)
            .Matches("^[^0-9]+$").WithMessage("State must not contain numbers.");
    }
}

public sealed class VerifyOtpValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpValidator()
    {
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Code).Matches("^[0-9]{6}$");
    }
}

public sealed class CreateFinancerValidator : AbstractValidator<CreateFinancerRequest>
{
    public CreateFinancerValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).NotEmpty();
        RuleFor(x => x.OwnerName).NotEmpty();
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Phone).Matches("^[+0-9 ()-]{8,24}$");
        RuleFor(x => x.PostalCode).NotEmpty();
        RuleFor(x => x.ServiceChargePercentage)
            .InclusiveBetween(0, 100)
            .When(x => x.ServiceChargePercentage.HasValue);
    }
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty();
        RuleFor(x => x.LastName).NotEmpty();
        RuleFor(x => x.Email).EmailAddress();
        RuleFor(x => x.Password)
            .MinimumLength(10)
            .Matches("[A-Z]")
            .Matches("[a-z]")
            .Matches("[0-9]");
        RuleFor(x => x.RoleIds).NotEmpty();
    }
}

public sealed class CustomerValidator : AbstractValidator<CreateCustomerRequest>
{
    public CustomerValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(160);
        RuleFor(x => x.DateOfBirth).LessThan(DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-18)));
        RuleFor(x => x.Phone).Matches("^[+0-9 ()-]{8,24}$");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.PostalCode).NotEmpty();
        RuleFor(x => x.Pan)
            .Matches("^[A-Z]{5}[0-9]{4}[A-Z]$")
            .When(x => !string.IsNullOrWhiteSpace(x.Pan));
        RuleFor(x => x.Aadhaar)
            .Matches("^[0-9]{12}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Aadhaar));
    }
}

public sealed class LoanProductValidator : AbstractValidator<LoanProductRequest>
{
    public LoanProductValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.MinimumPrincipal).GreaterThan(0);
        RuleFor(x => x.MaximumPrincipal).GreaterThanOrEqualTo(x => x.MinimumPrincipal);
        RuleFor(x => x.MinimumTenureMonths).GreaterThan(0);
        RuleFor(x => x.MaximumTenureMonths).GreaterThanOrEqualTo(x => x.MinimumTenureMonths);
        RuleFor(x => x.AnnualInterestRate).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaximumFoirPercentage).InclusiveBetween(1, 100);
    }
}

public sealed class EligibilityValidator : AbstractValidator<EligibilityRequest>
{
    public EligibilityValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.LoanProductId).NotEmpty();
        RuleFor(x => x.RequestedAmount).GreaterThan(0);
        RuleFor(x => x.TenureMonths).GreaterThan(0);
        RuleFor(x => x.MonthlyIncome).GreaterThan(0);
        RuleFor(x => x.MonthlyObligations).GreaterThanOrEqualTo(0);
    }
}

public sealed class ApplicationValidator : AbstractValidator<LoanApplicationRequest>
{
    public ApplicationValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.LoanProductId).NotEmpty();
        RuleFor(x => x.RequestedPrincipal).GreaterThan(0);
        RuleFor(x => x.RequestedAnnualRate).InclusiveBetween(0, 100);
        RuleFor(x => x.RequestedTenureMonths).GreaterThan(0);
        RuleFor(x => x.Purpose).NotEmpty().MaximumLength(500);
        RuleFor(x => x.MonthlyIncome).GreaterThan(0);
    }
}

public sealed class PaymentValidator : AbstractValidator<RecordPaymentRequest>
{
    public PaymentValidator()
    {
        RuleFor(x => x.LoanId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.ReceivedAt).LessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(5));
        RuleFor(x => x.ExternalReference).MaximumLength(100);
    }
}

public sealed class DirectLoanValidator : AbstractValidator<DirectLoanRequest>
{
    public DirectLoanValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.LoanProductId).NotEmpty();
        RuleFor(x => x.Principal).GreaterThan(0);
        RuleFor(x => x.AnnualInterestRate).InclusiveBetween(0, 100);
        RuleFor(x => x.TenureMonths).GreaterThan(0);
        RuleFor(x => x.DurationValue).GreaterThan(0).When(x => x.DurationValue.HasValue);
        RuleFor(x => x.InterestRate).GreaterThanOrEqualTo(0).When(x => x.InterestRate.HasValue);
    }
}

public sealed class TicketValidator : AbstractValidator<TicketRequest>
{
    public TicketValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(4000);
    }
}
