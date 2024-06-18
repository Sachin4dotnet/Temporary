using System.Net;
using SachinAdapter.Controllers;
using SachinAdapter.Filters.Exceptions;
using SachinAdapter.Models.Error;
using SachinAdapter.Models.Requests;
using SachinAdapter.Models.Requests.Payments;
using SachinAdapter.Models.Response;
using SachinAdapter.Services;
using SachinAdapter.Utilities;
using SacAz.Storage.Client;
using SacAz.Storage.Models.Payments.Outputs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using PbbaApiClient.Mastercard2Dsp;

namespace SachinAdapter.Test.Controllers;

public class PaymentsControllerTest
{
    private readonly Mock<IConfiguration> _configuration;
    private readonly PaymentsController _controller;
    private readonly Mock<IAgreementService> _mockAgreementService;
    private readonly Mock<ILogger<PaymentsController>> _mockLogger;
    private readonly Mock<IPaymentService> _mockPaymentService;
    private readonly Mock<IZappClient> _mockZappClient;

    public PaymentsControllerTest()
    {
        _mockLogger = new Mock<ILogger<PaymentsController>>();
        _mockPaymentService = new Mock<IPaymentService>();
        _configuration = new Mock<IConfiguration>();
        _mockAgreementService = new Mock<IAgreementService>();
        _mockZappClient = new Mock<IZappClient>();
        _controller = new PaymentsController(_mockLogger.Object, _mockPaymentService.Object, _configuration.Object,
            _mockAgreementService.Object, _mockZappClient.Object);
    }

    [Test]
    public async Task HelloPayment_should_return_string()
    {
        // Arrange

        //Act
        var response = await _controller.HelloPayment();

        //Assert
        response.Should().NotBeNull();
        response.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)response).Value.Should().BeEquivalentTo("Welcome to Zapp-Open Banking Adapter!");
    }

    [Test]
    public async Task PaymentRequest_should_return_response_model()
    {
        // Arrange
        var model = new NewPaymentRequestWithAgreement();
        model.MessageId = "7eab4eab35a542e085add0363a49c035";
        var responseObj = new MessageResponseBlock();
        responseObj.OriginalMessageId = "7eab4eab35a542e085add0363a49c035";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HelperClass.HeaderRequestId] = "7eab4eab35a542e085add0363a49c035";
        httpContext.Request.Headers[HelperClass.HeaderProductId] = "PBARFP";
        httpContext.Request.Headers[HelperClass.HeaderParticipantId] = "000545";
        httpContext.Request.Headers[HelperClass.HeaderJwsSignature] = "X-JWS-Signature";
        httpContext.Request.Headers[HelperClass.HeaderIdempotencyKey] = "Idempotency-Key";
        var pymtController = new PaymentsController(_mockLogger.Object, _mockPaymentService.Object,
            _configuration.Object, _mockAgreementService.Object, _mockZappClient.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        _mockPaymentService.Setup(_ =>
                _.InitiatePaymentRequest(model, It.IsAny<RequestHeader>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseObj);

        //Act
        var response = await pymtController.PaymentRequest(model);

        //Assert
        response.Should().NotBeNull();
        response.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)response).Value.Should().BeOfType(typeof(MessageResponseBlock));
        ((OkObjectResult)response).Value.Should().BeEquivalentTo(responseObj);
    }

    [Test]
    public async Task PaymentRequest_should_return_error_message_with_500()
    {
        // Arrange
        var model = new NewPaymentRequestWithAgreement();
        model.MessageId = "7eab4eab35a542e085add0363a49c035";
        var responseObj = new MessageResponseBlock();
        responseObj.OriginalMessageId = "7eab4eab35a542e085add0363a49c035";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HelperClass.HeaderRequestId] = "7eab4eab35a542e085add0363a49c035";
        httpContext.Request.Headers[HelperClass.HeaderProductId] = "PBARFP";
        httpContext.Request.Headers[HelperClass.HeaderParticipantId] = "000545";
        httpContext.Request.Headers[HelperClass.HeaderJwsSignature] = "X-JWS-Signature";
        httpContext.Request.Headers[HelperClass.HeaderIdempotencyKey] = "Idempotency-Key";
        var pymtController = new PaymentsController(_mockLogger.Object, _mockPaymentService.Object,
            _configuration.Object,
            _mockAgreementService.Object, _mockZappClient.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        _mockPaymentService.Setup(_ =>
                _.InitiatePaymentRequest(model, It.IsAny<RequestHeader>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                AdapterException.BadInputCode("Distributor not found while fetching key for connecting APv3."));

        //Act
        var exception = Assert.ThrowsAsync<AdapterException>(async () =>
            await pymtController.PaymentRequest(model));

        //Assert
        exception.ErrorMessage.Should().BeEquivalentTo("Distributor not found while fetching key for connecting APv3.");
        exception.ApiErrorCode.Should().Be(AdapterErrorCode.InvalidInput);
        exception.ApiHttpStatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetPaymentRequest_Ok_Response()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var pymtController =
            new PaymentsController(_mockLogger.Object, _mockPaymentService.Object, _configuration.Object,
                _mockAgreementService.Object, _mockZappClient.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

        //Act
        var response = await pymtController.GetPaymentDetails("123");

        //Assert
        response.Should().NotBeNull();
        response.Should().BeOfType<ObjectResult>();
        var okResult = (ObjectResult)response;
        okResult.Value.Should().NotBeNull();
    }

    [Test]
    public async Task GetPaymentRequest_Query_Ok_Response()
    {
        // Arrange
        var queryCollection = new Dictionary<string, StringValues>
        {
            { "ReturnPaymentResponse", "true" }
        };
        var httpContext = new DefaultHttpContext();

        httpContext.Request.Query = new QueryCollection(queryCollection);
        var pymtController =
            new PaymentsController(_mockLogger.Object, _mockPaymentService.Object, _configuration.Object,
                _mockAgreementService.Object, _mockZappClient.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        var paymentResponse = new CreateAcceptPaymentOutput
        {
            PaymentId = Guid.NewGuid(),
            FlowUrl = "www.google.com"
        };
        _mockPaymentService.Setup(c => c.CreatePayment(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(paymentResponse);
        //Act
        var response = await pymtController.GetPaymentDetails("123");

        //Assert
        response.Should().NotBeNull();
        response.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)response;
        okResult.Value.Should().NotBeNull();
    }

    [Test]
    public async Task GetPaymentRequest_Query_Ok_ResponseAgreement()
    {
        // Arrange
        var queryCollection = new Dictionary<string, StringValues>
        {
            { HelperClass.QueryUserCaseType, "Agreement" }
        };
        var httpContext = new DefaultHttpContext();

        httpContext.Request.Query = new QueryCollection(queryCollection);
        var pymtController =
            new PaymentsController(_mockLogger.Object, _mockPaymentService.Object, _configuration.Object,
                _mockAgreementService.Object, _mockZappClient.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        var agreementResponse = new CreateMandateOutput
        {
            MandateId = Guid.NewGuid(),
            FlowUrl = "www.google.com"
        };
        _mockAgreementService.Setup(c => c.CreateMandate(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(agreementResponse);
        //Act
        var response = await pymtController.GetPaymentDetails("123");

        //Assert
        response.Should().NotBeNull();
        response.Should().BeOfType<RedirectResult>();
        var okResult = (RedirectResult)response;
        okResult.Should().NotBeNull();
        okResult.Url.Should().NotBeNull();
        okResult.Url.Should().Be(agreementResponse.FlowUrl);
    }

    [Test]
    public async Task GetPaymentRequest_Query_BadRequest_Response()
    {
        // Arrange
        var queryCollection = new Dictionary<string, StringValues>
        {
            { "ReturnPaymentResponse", "true" }
        };
        var httpContext = new DefaultHttpContext();

        httpContext.Request.Query = new QueryCollection(queryCollection);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        _mockPaymentService.Setup(c => c.CreatePayment(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new AdapterException(AdapterUhpCodes.UHP005, HttpStatusCode.BadRequest,
                "LifecycleId passed is not valid or not found yet in Storage"));

        //Act
        var exception = Assert.ThrowsAsync<AdapterException>(async () =>
            await _controller.GetPaymentDetails("123"));

        //Assert
        Assert.AreEqual(AdapterUhpCodes.UHP005, exception.AdapterErrorCode);
        Assert.AreEqual("LifecycleId passed is not valid or not found yet in Storage", exception.ErrorMessage);
        Assert.AreEqual(HttpStatusCode.BadRequest, exception.ApiHttpStatusCode.Value);
    }

    [Test]
    public async Task GetPaymentRequest_Should_Handle_401FromSacAz()
    {
        // Arrange
        var queryCollection = new Dictionary<string, StringValues>
        {
            { "ReturnPaymentResponse", "true" }
        };
        var httpContext = new DefaultHttpContext();

        httpContext.Request.Query = new QueryCollection(queryCollection);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        var SacAzException = new SacAzStorageClientException("Unauthorized message", 401,
            "Unauthorized Response", new Exception());

        _mockPaymentService.Setup(c => c.CreatePayment(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(SacAzException);

        //Act
        var exception = Assert.ThrowsAsync<AdapterException>(async () =>
            await _controller.GetPaymentDetails("123"));

        //Assert
        exception.Should().BeOfType<AdapterException>();
        Assert.AreEqual(AdapterErrorCode.ProviderDisabled, exception.ApiErrorCode);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, exception.ApiHttpStatusCode.Value);
    }

    [Test]
    public async Task WebHook_Return_CorrectResponse_AcceptPaymentStatusUpdated()
    {
        //Arrange
        var requestbody = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.Parse("98fe7ae7-2601-40c1-8956-eac246c490d2"),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0
        };

        var expectedPaymentStatus = new PaymentConfirmationAdvicesResponse
        {
            initiatingPartyId = "123",
            messageId = "123",
            creationDateTime = DateTime.Now,
            originalMessageId = "123",
            paymentRequestLifecycleId = "123",
            requestStatus = new RequestStatus { paymentRequestStatus = "APRD" },
            agreementStatus = new AgreementStatus { agreementId = "123", agreementStatus = "APRD" }
        };

        _mockPaymentService.Setup(service => service.GetPaymentStatusWebhook(It.IsAny<WebhookRequest>(),
                null))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedPaymentStatus, 200)));

        var controller = new PaymentsController(_mockLogger.Object, _mockPaymentService.Object, _configuration.Object,
            _mockAgreementService.Object, _mockZappClient.Object);

        //Act
        var result = await controller.WebHook(requestbody);

        //Assert
        Assert.NotNull(result);

        Assert.IsInstanceOf<ObjectResult>(result);

        var okObjectResult = result as ObjectResult;
        Assert.NotNull(okObjectResult);
        Assert.AreEqual(200, okObjectResult.StatusCode);
        Assert.AreEqual(expectedPaymentStatus, okObjectResult.Value);
    }

    [Test]
    public async Task WebHook_Return_CorrectResponse_MandateStatusUpdated()
    {
        //Arrange
        var requestbody = new WebhookRequest
        {
            Type = "MandateStatusUpdated",
            Data = new MandateStatusUpdated()
            {
                MandateId = Guid.Parse("98fe7ae7-2601-40c1-8956-eac246c490d2"),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0
        };

        var expectedPaymentStatus = new PaymentConfirmationAdvicesResponse
        {
            initiatingPartyId = "123",
            messageId = "123",
            creationDateTime = DateTime.Now,
            originalMessageId = "123",
            paymentRequestLifecycleId = "123",
            requestStatus = new RequestStatus { paymentRequestStatus = "APRD" },
            agreementStatus = new AgreementStatus { agreementId = "123", agreementStatus = "APRD" }
        };

        _mockAgreementService.Setup(service => service.GetAgreementStatusWebhook(It.IsAny<WebhookRequest>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedPaymentStatus, 200)));

        var controller = new PaymentsController(_mockLogger.Object, _mockPaymentService.Object, _configuration.Object,
            _mockAgreementService.Object, _mockZappClient.Object);

        //Act
        var result = await controller.WebHook(requestbody);

        //Assert
        Assert.NotNull(result);

        Assert.IsInstanceOf<ObjectResult>(result);

        var okObjectResult = result as ObjectResult;
        Assert.NotNull(okObjectResult);
        Assert.AreEqual(200, okObjectResult.StatusCode);
        Assert.AreEqual(expectedPaymentStatus, okObjectResult.Value);
    }

    [Test]
    public async Task PaymentConfirmation_Returns_OK_With_Valid_PaymentStatus()
    {
        //Arrange
        var paymentRequestLifecycleId = "472e651e-5a1e-424d-8098-23858bf03ad7";
        var expectedPaymentStatus = new PaymentRequestStatusRetrievalAck
        {
            MessageId = "12345",
            OriginalMessageId = "123",
            CreationDateTime = DateTime.Now,
            InitiatingPartyId = "123"
        };

        var input = new NewPaymentRequestStatusRetrieval
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = DateTime.Now,
            BusinessType = 3,
            PaymentRequestLifecycleId = "923123123123123100",
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111",
            Debtor = new Debtor
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            }
        };

        _mockPaymentService.Setup(service => service.GetPaymentStatus(paymentRequestLifecycleId, input))
            .ReturnsAsync(expectedPaymentStatus);

        //Act
        var result = await _controller.PaymentConfirmation(paymentRequestLifecycleId, input) as OkObjectResult;

        //Assert
        Assert.AreEqual(expectedPaymentStatus, result.Value);
        Assert.AreEqual(200, result.StatusCode);
    }

    [Test]
    public async Task InitiatePayment_Return_Ok_Result()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();

        var createPaymentRequestPayload = new CreatePaymentRequestPayload();
        _mockPaymentService.Setup(service =>
            service.CreatePaymentRequestApi(It.IsAny<CreatePaymentRequestPayload>(), It.IsAny<IHeaderDictionary>()));

        httpContext.Request.Headers[HelperClass.HeaderRequestId] = "7eab4eab35a542e085add0363a49c035";
        httpContext.Request.Headers[HelperClass.HeaderProductId] = "PBARFP";
        httpContext.Request.Headers[HelperClass.HeaderParticipantId] = "000545";
        httpContext.Request.Headers[HelperClass.HeaderJwsSignature] = "X-JWS-Signature";
        httpContext.Request.Headers[HelperClass.HeaderIdempotencyKey] = "Idempotency-Key";
        var pymtController = new PaymentsController(_mockLogger.Object, _mockPaymentService.Object,
            _configuration.Object, _mockAgreementService.Object, _mockZappClient.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
        //Act
        var result = await pymtController.InitiatePayment(createPaymentRequestPayload) as OkObjectResult;

        //Assert
        Assert.AreEqual(200, result.StatusCode);
    }

    [Test]
    public async Task PaymentConfirmation_Returns_NotFound_When_Payment_Not_Found()
    {
        //Arrange
        var paymentRequestLifecycleId = "472e651e";

        var input = new NewPaymentRequestStatusRetrieval
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = DateTime.Now,
            BusinessType = 3,
            PaymentRequestLifecycleId = "923123123123123100",
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111",
            Debtor = new Debtor
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            }
        };

        _mockPaymentService.Setup(service => service.GetPaymentStatus(paymentRequestLifecycleId, input))
            .ThrowsAsync(new AdapterException(AdapterUhpCodes.UHP005, HttpStatusCode.BadRequest,
                "Payment with zappPaymentId is not found."));

        //Act
        var exception = Assert.ThrowsAsync<AdapterException>(async () =>
            await _controller.PaymentConfirmation(paymentRequestLifecycleId, input));

        //Assert
        Assert.AreEqual(AdapterUhpCodes.UHP005, exception.AdapterErrorCode);
        Assert.AreEqual("Payment with zappPaymentId is not found.", exception.ErrorMessage);
        Assert.AreEqual(HttpStatusCode.BadRequest, exception.ApiHttpStatusCode.Value);
    }

    [Test]
    public async Task WebHook_Return_BadRequest_Response()
    {
        //Arrange
        var requestbody = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.Parse("98fe7ae7-2601-40c1-8956-eac246c490d2"),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0
        };

        _mockPaymentService.Setup(service => service.GetPaymentStatusWebhook(It.IsAny<WebhookRequest>(), null))
            .ThrowsAsync(new AdapterException(AdapterUhpCodes.UHP005, HttpStatusCode.BadRequest,
                "Payment with zappPaymentId is not found."));

        //Act
        var exception = Assert.ThrowsAsync<AdapterException>(async () =>
            await _controller.WebHook(requestbody));

        //Assert
        Assert.AreEqual(AdapterUhpCodes.UHP005, exception.AdapterErrorCode);
        Assert.AreEqual("Payment with zappPaymentId is not found.", exception.ErrorMessage);
        Assert.AreEqual(HttpStatusCode.BadRequest, exception.ApiHttpStatusCode.Value);
    }
}