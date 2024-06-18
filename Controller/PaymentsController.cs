using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using SachinAdapter.AzureTables;
using SachinAdapter.Extensions;
using SachinAdapter.Filters;
using SachinAdapter.Filters.Exceptions;
using SachinAdapter.Models.Enums;
using SachinAdapter.Models.Requests.Payments;
using SachinAdapter.Models.Response;
using SachinAdapter.Services;
using SachinAdapter.Utilities;
using SacAz.Storage.Client;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace SachinAdapter.Controllers;

[ApiController]
[Route("[controller]")]
[ValidateModel]
public class PaymentsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentsController> _logger;
    private readonly IPaymentService _paymentService;

    public PaymentsController(ILogger<PaymentsController> logger, IPaymentService paymentService,
        IConfiguration configuration)
    {
        _logger = logger;
        _paymentService = paymentService;
        _configuration = configuration;
    }

    /// <summary>
    /// Initiates a payment request with an agreement.
    /// </summary>
    /// <remarks>
    /// This endpoint initiates a payment request with an agreement, using the provided data in the request body.
    /// The request body should contain information required for the payment request with the agreement.
    /// </remarks>
    /// <param name="model">The model containing data for the payment request with the agreement.</param>
    /// <returns>An IActionResult representing the result of the payment request initiation.</returns>
    [HttpPost]
    [Route("/agreement-payment-requests")]
    [PaymentHeader]
    public async Task<IActionResult> PaymentRequest(NewPaymentRequestWithAgreement model)
    {
        if (!ModelState.IsValid) return StatusCode(StatusCodes.Status400BadRequest, ModelState);

        try
        {
            var headers = HelperClass.GetRequestHeaders(Request.Headers);
            var response = await _paymentService.InitiatePaymentRequest(model, headers);
            string jwsSignature = headers.HeaderJwsSignature;
            try
            {
                // jwsSignature = await _apiClient.GenerateJwsSignature(response);
            }
            catch (Exception e)
            {
                _logger.LogError("jws signature failed:-" + e.Message);
            }

            HttpContext.Response.Headers.Append(HelperClass.HeaderJwsSignature, jwsSignature);
            return Ok(response);
        }
        catch (AdapterException ex)
        {
            throw ex;
        }
        catch (Exception ex)
        {
            _logger.LogError(500, ex.Message);
            return StatusCode(500, ex.Message);
        }
    }

    /// <summary>
    /// Handle incoming webhook requests.
    /// </summary>
    /// <remarks>
    /// This endpoint receives webhook requests from external systems and processes them accordingly.
    /// The endpoint expects a JSON payload containing information about the webhook request, including its type and data.
    /// Depending on the type of webhook request received, this endpoint performs different actions.
    /// </remarks>
    /// <param name="requestBody">The JSON payload containing information about the webhook request.</param>
    /// <returns>An IActionResult representing the result of processing the webhook request.</returns>
    [HttpPost]
    [Route("/webhook")]
    public async Task<IActionResult> WebHook(
        [FromBody] [JsonConverter(typeof(WebhookRequestConverter))]
        WebhookRequest requestBody)
    {
        try
        {
            if (!ModelState.IsValid) return StatusCode(StatusCodes.Status400BadRequest, ModelState);

            var webhookData =
                WebhookDataFactory.CreateWebhookData(requestBody.Type, requestBody.Data, requestBody.RetryCount);
            switch (requestBody.Type)
            {
                case "AcceptPaymentStatusUpdated":
                    var (resultPayment, statusCodePayment) = await _paymentService.GetPaymentStatusWebhook(webhookData);
                    return StatusCode(statusCodePayment, resultPayment);
                default:
                    var (resultMandate, statusCodeMandate) =
                        await _agreementService.GetAgreementStatusWebhook(requestBody);
                    return StatusCode(statusCodeMandate, resultMandate);
            }
        }
        catch (AdapterException ex)
        {
            throw ex;
        }
        catch (Exception ex)
        {
            if (ex is SacAzStorageClientException)
            {
                _logger.LogError(400, "SacAz Storage client Exception:- " + ex);
                var clientExc = ex as SacAzStorageClientException;
                if (clientExc.StatusCode == 401)
                    throw AdapterException.SacAzPaymentConnectionFailed(
                        "Connection with Payment system is not authorized.");
                throw AdapterException.SacAzPaymentConnectionFailed(
                    $"Payment system Connection failed with status {clientExc.StatusCode} and message {clientExc.Message}.");

                return StatusCode(400, "SacAz Storage client Exception " + ex);
            }

            _logger.LogError("Exception. " + ex);
            return StatusCode(500, "An error occurred while processing the payment. " + ex);
        }
    }

    /// <summary>
    /// Retrieves the status of a payment request.
    /// </summary>
    /// <remarks>
    /// This endpoint retrieves the status of a payment request identified by its lifecycle ID.
    /// The endpoint expects the lifecycle ID of the payment request as a route parameter, and additional
    /// information about the status retrieval request in the request body.
    /// </remarks>
    /// <param name="payment_request_lifecycle_id" example="12345678912345">The lifecycle ID of the payment request.</param>
    /// <param name="model">The model containing additional information about the status retrieval request.</param>
    /// <returns>An IActionResult representing the status retrieval result.</returns>
    [HttpPost]
    [Route("/payment-requests/{payment_request_lifecycle_id}/status-retrievals")]
    [PaymentHeader]
    public async Task<IActionResult> PaymentConfirmation([Required] [FromRoute] string? payment_request_lifecycle_id,
        NewPaymentRequestStatusRetrieval model)
    {
        if (!ModelState.IsValid) return StatusCode(StatusCodes.Status400BadRequest, ModelState);
        //Done_TO_DO:logic to get the payment Status from APv3.
        try
        {
            var statusResult = await _paymentService.GetPaymentStatus(payment_request_lifecycle_id, model);
            return Ok(statusResult);
        }
        catch (AdapterException ex)
        {
            throw ex;
        }
    }

    [HttpPost]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Route("/InitiatePayment")]
    public async Task<IActionResult> InitiatePayment([FromBody] CreatePaymentRequestPayload createPaymentRequestPayload)
    {
        var headerDictionary = HttpContext.Request.Headers;

        var response = await _paymentService.CreatePaymentRequestApi(createPaymentRequestPayload, headerDictionary);

        if (response != null)
        {
            var res = new NewPaymentRequestWithAgreement
            {
                InitiatingPartyId = createPaymentRequestPayload.initiatingPartyId,
                MessageId = createPaymentRequestPayload.messageId,
                CreationDateTime = DateTime.Parse(createPaymentRequestPayload.creationDateTime),
                BusinessType = createPaymentRequestPayload.businessType,
                Creditor = new CreditorForPWA
                {
                    CreditorId = createPaymentRequestPayload.creditor.creditorId,
                    CreditorServiceProviderId = createPaymentRequestPayload.creditor.creditorServiceProviderId,
                    CreditorReturnString = createPaymentRequestPayload.creditor.creditorReturnString,


                    // test value added due tp not having suffient data
                    CreditorLogoUrl = "test",
                    CreditorCategoryCode = _configuration["ZappSettings:creditorCategoryCode"],
                    CreditorTradeName = _configuration["ZappSettings:creditorTradeName"],
                    CreditorAccount = new CreditorAccount
                    {
                        AccountNumber = _configuration["ZappSettings:accountNumber"],
                        AccountName = "test",
                        AccountType = "test"
                    }
                },
                Transaction = new Transaction
                {
                    AgreementId = "test",
                    PaymentRequestLifecycleId = response.transaction.paymentRequestLifecycleId,
                    EndToEndId = createPaymentRequestPayload.transaction.endToEndId,
                    InstructionId = createPaymentRequestPayload.transaction.instructionId,
                    ConfirmationExpiryTimeInterval = response.transaction.confirmationExpiryTimeInterval,
                    PaymentRequestType = createPaymentRequestPayload.transaction.paymentRequestType,
                    TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                    InstructedAmount = new Amount
                    {
                        Currency = createPaymentRequestPayload.transaction.instructedAmount.currency,
                        Value = (double)createPaymentRequestPayload.transaction.instructedAmount.value
                    },

                    Purpose = "ONLN",
                    CategoryPurpose = CategoryPurpose.PYMT.ToString()
                },
                Debtor = new DebtorForPWA
                {
                    DebtorId = "test",
                    DebtorServiceProviderId = "test"
                }
            };
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            await PaymentRequest(res);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        return Ok(response);
    }


    [HttpPost]
    [Route("/test/payment_confirmation_advices/{PAYMENT_LIFE_CYCLE_ID}")]
    [PaymentHeader]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ExcludeFromCodeCoverage]
    public async Task<IActionResult> Post([Required] [FromRoute] string PAYMENT_LIFE_CYCLE_ID,
        NewPaymentConfirmationAdvice pca)
    {
        try
        {
            var headers = HelperClass.GetRequestHeaders(Request.Headers);
            //Done_TO_DO:Add logic here 
            var (result, statusCode) =
                await _paymentService.PaymentConfirmationAdvices(PAYMENT_LIFE_CYCLE_ID, pca, headers);
            return StatusCode(statusCode, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(500, ex.Message);
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost]
    [Route("/test/Distributor")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ExcludeFromCodeCoverage]
    public async Task<IActionResult> AddDistributor(Merchant model)
    {
        try
        {
            var test =
                await _paymentService.AddDistributor(model);
            return StatusCode(200, test);
        }
        catch (Exception ex)
        {
            _logger.LogError(500, ex.Message);
            return StatusCode(500, ex.Message);
        }
    }
}