using SachinAdapter.Models.Requests.Payments;
using FluentAssertions;

namespace SachinAdapter.Test.Models.Requests.Payments;

public class WebhookRequestTest
{
    [Test]
    public void WebHookRequest_Validation_ShouldPassForValidData()
    {
        //Arrange
        var validRequest = new WebhookRequest()
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.NewGuid(),
                ExecutionTime = DateTime.UtcNow
            },
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var validator = new WebhookRequestValidator();

        //Act
        var validationResult = validator.Validate(validRequest);

        //Assert
        validationResult.IsValid.Should().BeTrue();
    }

    [TestCase("111123123123123111")]
    [TestCase(null)]
    public void WebhookRequest_Validation_ShouldPassForValidData(string lifecycleId)
    {
        //Arrange
        var validWebhookRequest = new WebhookRequest()
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.NewGuid(),
                ExecutionTime = DateTime.UtcNow
            },
            PaymentRequestStatusRetrievalLifecycleId = lifecycleId
        };

        var validator = new WebhookRequestValidator();

        //Act
        var validationResult = validator.Validate(validWebhookRequest);

        //Assert
        validationResult.IsValid.Should().BeTrue();
    }

    [Test]
    public void WebhookRequest_Validation_ShouldFailForInvalidPaymentId()
    {
        //Arrange
        var invalidWebhookRequest = new WebhookRequest()
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId =Guid.Empty,
                ExecutionTime = DateTime.UtcNow
            },
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var validator = new WebhookRequestValidator();
        
        //Act
        var validationResult = validator.Validate(invalidWebhookRequest);
        
        //Assert
        validationResult.IsValid.Should().BeFalse();
        validationResult.Errors.Should().Contain(error => error.PropertyName == "Data.PaymentId");
    }
}