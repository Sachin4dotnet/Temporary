using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using SachinAdapter.Extensions;
using FluentValidation;

namespace SachinAdapter.Models.Requests.Payments;

public class WebhookRequest
{
    public string? Type { get; set; }

    public IWebhookData Data { get; set; }

    public int RetryCount { get; set; }
    public string? PaymentRequestStatusRetrievalLifecycleId { get; set; }
}

public interface IWebhookData
{
}

public class AcceptPaymentStatusUpdated : IWebhookData
{
    public Guid PaymentId { get; set; }
    public DateTimeOffset ExecutionTime { get; set; }
}

public class MandateStatusUpdated : IWebhookData
{
    public Guid MandateId { get; set; }
    public DateTimeOffset ExecutionTime { get; set; }
}

[ExcludeFromCodeCoverage]
public static class WebhookDataFactory
{
    public static WebhookRequest CreateWebhookData(string type, dynamic data, int retryCount)
    {
        switch (type)
        {
            case "AcceptPaymentStatusUpdated":
                var acceptPaymentData = new AcceptPaymentStatusUpdated()
                {
                    PaymentId = data.PaymentId,
                    ExecutionTime = data.ExecutionTime
                };
                return new WebhookRequest()
                {
                    Type = type,
                    Data = acceptPaymentData,
                    RetryCount = retryCount
                };
            case "MandateStatusUpdated":
                var mandateData = new MandateStatusUpdated()
                {
                    MandateId = data.MandateId,
                    ExecutionTime = data.ExecutionTime
                };
                return new WebhookRequest()
                {
                    Type = type,
                    Data = mandateData,
                    RetryCount = retryCount
                };
            default:
                throw new ArgumentException("Unsupported webhook type");
        }
    }
}

[ExcludeFromCodeCoverage]
public class WebhookDataValidator<T> : AbstractValidator<IWebhookData> where T : IWebhookData
{
    public WebhookDataValidator()
    {
        RuleFor(x => x)
            .NotNull().WithMessage($"{typeof(T).Name} data must not be null.")
            .NotEmpty().WithMessage($"{typeof(T).Name} data must not be empty.");

        // Add additional validation rules specific to your data types if needed
        if (typeof(T) == typeof(AcceptPaymentStatusUpdated))
        {
            RuleFor(x => (x as AcceptPaymentStatusUpdated).PaymentId)
                .NotEmpty().WithMessage("PaymentId must not be empty.");
        }
        else if (typeof(T) == typeof(MandateStatusUpdated))
        {
            RuleFor(x => (x as MandateStatusUpdated).MandateId)
                .NotEmpty().WithMessage("MandateId must not be empty.");
        }
    }
}

[ExcludeFromCodeCoverage]
public class WebhookRequestValidator : AbstractValidator<WebhookRequest>
{
    public WebhookRequestValidator()
    {
        RuleFor(x => x.Type)
            .NotNull().WithMessage("Type must not be null.")
            .NotEmpty().WithMessage("Type must not be empty.")
            .Must(x => x == "AcceptPaymentStatusUpdated" || x == "MandateStatusUpdated")
            .WithMessage("Unsupported webhook type.");

        RuleFor(x => x.RetryCount)
            .GreaterThanOrEqualTo(0).WithMessage("RetryCount must be greater than or equal to 0.");

        When(x => x.Type == "AcceptPaymentStatusUpdated", () =>
        {
            RuleFor(x => x.Data)
                .NotNull().WithMessage("Data must not be null.")
                .SetValidator(new WebhookDataValidator<AcceptPaymentStatusUpdated>())
                .WithMessage("Invalid AcceptPaymentStatusUpdated data.");
        });

        When(x => x.Type == "MandateStatusUpdated", () =>
        {
            RuleFor(x => x.Data)
                .NotNull().WithMessage("Data must not be null.")
                .SetValidator(new WebhookDataValidator<MandateStatusUpdated>())
                .WithMessage("Invalid MandateStatusUpdated data.");
        });
    }
}
