using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SachinAdapter.AzureServices.Storage;
using SachinAdapter.AzureTables;
using SachinAdapter.Filters.Exceptions;
using SachinAdapter.Models.AzureTables;
using SachinAdapter.Models.AzureTables.StorageEntity;
using SachinAdapter.Models.Enums;
using SachinAdapter.Models.Requests;
using SachinAdapter.Models.Requests.Payments;
using SachinAdapter.Models.Response;
using SachinAdapter.Utilities;
using SacAz.Storage.Client;
using SacAz.Storage.Models.Payments;
using SacAz.Storage.Models.Payments.Inputs;
using SacAz.Storage.Models.Payments.Outputs;
using AutoMapper;
using Jose;
using Newtonsoft.Json;
using PbbaApiClient.Dsp2Mastercard;
using PbbaApiClient.Mastercard2Dsp;
using Amount = PbbaApiClient.Dsp2Mastercard.Amount;
using Debtor = PbbaApiClient.Dsp2Mastercard.Debtor;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Payment = SachinAdapter.AzureTables.Payment;
using Status = PbbaApiClient.Dsp2Mastercard.Status;

namespace SachinAdapter.Services;

public interface IPaymentService
{
    Task<MessageResponseBlock> InitiatePaymentRequest(NewPaymentRequestWithAgreement model, RequestHeader headers,
        CancellationToken cancellationToken = default);

    public Task<CreateAcceptPaymentOutput> CreatePayment(string lifeCycleId, string providerId);
    Task<PaymentRequestStatusRetrievalAck> GetPaymentStatus(string lifecycleId, NewPaymentRequestStatusRetrieval model);

    Task<(object result, int statusCode)> GetPaymentStatusWebhook(WebhookRequest webhookRequest,
        PaymentStorageEntity? paymentEntity = null);

    Task<PaymentStorageEntity?> GetPaymentFromCache(string lifeCycleId, CancellationToken cancellationToken = default);

    Task<CprResponse> CreatePaymentRequestApi(CreatePaymentRequestPayload createPaymentRequestPayload,
        IHeaderDictionary headerDictionary);

    Task<(object result, int statusCode)> PaymentConfirmationAdvices(string PAYMENT_LIFE_CYCLE_ID,
        NewPaymentConfirmationAdvice pca, RequestHeader header);

    Task<bool> AddDistributor(Merchant entity,
        CancellationToken cancellationToken = default);
}

public class PaymentService : IPaymentService
{
    private readonly IAgreementService _agreementService;
    private readonly ISacAzStorageClient _sacazStorageClient;
    private readonly IConcurrentMemoryCache _concurrentMemoryCache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentService> _logger;
    private readonly IMapper _mapper;
    private readonly IMerchantService _merchantService;
    private readonly IPaymentPayloadService _paymentPayloadService;
    private readonly IStorageService<PaymentStorageEntity> _paymentStorageService;
    private readonly IStorageService<PaymentCnfAdviseResponseStorageEntity> _pymtResponseStorageService;
    private readonly IZappClient _zappClient;

    public PaymentService(
        ISacAzStorageClient sacazStorageClient,
        IConfiguration configuration,
        IMapper mapper,
        IConcurrentMemoryCache concurrentMemoryCache,
        IMerchantService merchantService,
        IPaymentPayloadService paymentPayloadService,
        IStorageService<PaymentStorageEntity> paymentStorageService,
        IStorageService<PaymentCnfAdviseResponseStorageEntity> pymtResponseStorageService,
        IZappClient zappClient,
        IAgreementService agreementService,
        ILogger<PaymentService> logger)
    {
        _sacazStorageClient = sacazStorageClient;
        _configuration = configuration;
        _mapper = mapper;
        _concurrentMemoryCache = concurrentMemoryCache;
        _merchantService = merchantService;
        _paymentPayloadService = paymentPayloadService;
        _paymentStorageService = paymentStorageService;
        _pymtResponseStorageService = pymtResponseStorageService;
        _zappClient = zappClient;
        _agreementService = agreementService;
        _logger = logger;
    }

    public async Task<MessageResponseBlock> InitiatePaymentRequest(NewPaymentRequestWithAgreement model,
        RequestHeader headers,
        CancellationToken cancellationToken = default)
    {
        // Get the client_id and secret for distributor
        var zappDistributorId = model.Creditor.CreditorServiceProviderId;
        var zappMerchantId = model.Creditor.CreditorId;
        var creditor = JsonSerializer.Serialize(model.Creditor);
        var merchantData = headers.HeaderParticipantId + "-" + creditor;
        var merchantDataHash = HelperClass.CreateMd5(merchantData);

        // first try to get merchant from cache & adapter datastorage 
        var merchant = await _merchantService.GetMerchantFromDb(zappDistributorId);
        // if (merchant == null)
        //     merchant = await _merchantService.CreateDestinationAndSaveBlobCache(model.Creditor, zappMerchantId,
        //         zappDistributorId,
        //         merchantDataHash, cancellationToken);

        // TODO: below should be done after we have update merchant on APv3
        // // check for MerchantDataHash and if not same enforce update
        // else if (merchant.MerchantDataHash != merchantDataHash)
        // {
        //     // Delete the existing merchant from Blob Data & Cache
        //     var isRemoved = await _merchantService.DeleteMerchantFromBlobAnd Cache(
        //         merchant.ToStorageEntity(), cancellationToken); 
        //  
        //     // Update Merchant [Destination] to APv3
        //     merchant = await CreateDestinationAndSaveBlobCache(model, merchant, merchantDataHash, cancellationToken);
        // }

        // Storing the Request Payload into Blob DB
        var requestPayload = _mapper.Map<PaymentPayload>(model);
        requestPayload.Headers = headers;
        var payload = new PaymentPayloadStorageEntity
        {
            Id = model.Transaction.PaymentRequestLifecycleId,
            RequestPayload = requestPayload
        };
        var resultPayload = await _paymentPayloadService.CreatePaymentPayloadToDb(payload, cancellationToken);

        // Storing the Payment object into Blob DB -- will update same object after APv3 CreatePayment
        var resultPayment = await SavePaymentToStorageFirstTime(model, merchant, cancellationToken);

        // DONE TO_DO : Remove below static response after adding logic
        // Resolved: Query : Not sure if we need to generate messageId or have to fetch from request
        var newMessageId = Guid.NewGuid().ToString().Replace("-", "");
        var response = new MessageResponseBlock
        {
            InitiatingPartyId = Convert.ToString(_configuration.GetValue<string>("PBBA_DSP_Id")!),
            MessageId = newMessageId,
            CreationDateTime = DateTime.UtcNow,
            OriginalMessageId = model.MessageId
        };
        return response;
    }

    public async Task<CprResponse> CreatePaymentRequestApi(
        CreatePaymentRequestPayload createPaymentRequestPayload, IHeaderDictionary headerDictionary)
    {
        var apiUrl = _configuration["ZappSettings:createPaymentApiUrl"];

        var kid = _configuration["ZappSettings:kid"];

        var header = new Dictionary<string, object>
        {
            { "alg", "RS256" },
            { "kid", kid },
            { "crit", new List<string> { "iat" } },
            { "iat", UnixTimestampFromDateTime(DateTime.UtcNow).ToString() }
        };

        var key = _configuration.GetValue<string>("ZappSettings:DistributorSigningPrivateKey");
        var rsaKey = ReadPrivateKey(key);
        var jws = JWT.Encode(createPaymentRequestPayload, rsaKey, JwsAlgorithm.RS256, header);

        var jwsParts = jws.Split('.');

        var jwsHeaders = jwsParts[0].Replace("=", "");

        var jwsSignature = jwsParts[2];

        var detachedJws = jwsHeaders + ".." + jwsSignature;

        var jsonBody = JsonConvert.SerializeObject(createPaymentRequestPayload);

        return await _zappClient.InitiatePaymentPostRequest(apiUrl, jsonBody, detachedJws);
    }

    public async Task<CreateAcceptPaymentOutput> CreatePayment(string lifeCycleId, string providerId)
    {
        var payment = await GetZappPaymentId(lifeCycleId);

        var paymentPayload = await _paymentPayloadService.GetPaymentPayloadFromDb(payment.ZappPaymentId);

        var merchant = await _merchantService.GetMerchantFromDb(payment.ZappDistributorId);

        var bearerToken = HelperClass.GetJwtForDistributor(_configuration, merchant.TenantId);

        if (paymentPayload.Transaction.AgreementId == HelperClass.AgreementId)
        {
            var input = new CreateAcceptPaymentInput
            {
                Amount = Convert.ToDecimal(paymentPayload.Transaction.InstructedAmount.Value),
                DestinationId = Guid.Parse(merchant.DestinationId),
                RedirectUrl = paymentPayload.Creditor.CreditorReturnString,
                ProviderId = providerId == null ? providerId : _configuration["DefaultBank"],
                Currency = paymentPayload.Transaction.InstructedAmount.Currency,
                SchemeId = SchemeId.UkFasterPayments.ToString(),
                Context = new PaymentContext()
                {
                    PurposeCode = paymentPayload.Transaction.Purpose
                    // ContextCode = Enum.TryParse(paymentPayload.Transaction.CategoryPurpose.ToString(),
                    //     out CategoryPurpose mandateEnums)
                    //     ? MandateMapper.MapMandateEnumToSacAzEnum(mandateEnums).ToString()
                    //     : default(SacAzMandateEnum).ToString()
                },
                //Reference = paymentPayload.Transaction.EndToEndId,
                EndUser = new EndUser()
                {
                    Id = paymentPayload.Creditor.CreditorId
                }
            };

            var sacazContextCode =
                MandateMapper.MapCategoryPurposeToSacAzContextCode(paymentPayload.Transaction.CategoryPurpose);
            if (!string.IsNullOrEmpty(sacazContextCode))
                input.Context.ContextCode = sacazContextCode;

            var _outputV1 = await _sacazStorageClient.PaymentService.CreateAcceptPayment(bearerToken, input);

            payment.SacAzPaymentId = Convert.ToString(_outputV1.PaymentId)!;
            payment.SacAzPaymentUrl = _outputV1.FlowUrl;

            var result = await _paymentStorageService.Update(payment);

            if (result.IsSuccess)
                return _outputV1;
            throw new Exception("Payment not updated successfully");
        }

        //get the mandate id from agreement blob 
        var mandate = await _agreementService.GetAgreementById(paymentPayload.Transaction.AgreementId);

        //return the data from the method that we create to call the mandate/{mandateId}/payments
        if (mandate.SacAzMandateId != null && !string.IsNullOrEmpty(mandate.SacAzMandateId))
        {
            var url = _configuration["PaymentUri"] + "/mandates";
            //TODO:Need to get this data from the mandate itself as of now the flow is broken 
            var input_mandate = new CreatePaymentUsingMandateInput
            {
                Currency = paymentPayload.Transaction.InstructedAmount.Currency,
                Amount = Convert.ToDecimal(paymentPayload.Transaction.InstructedAmount.Value)
            };
            var output =
                await CreateMandateUsingPayment(url, Guid.Parse(mandate.SacAzMandateId), bearerToken, input_mandate);
            //mandate.SacAzMandateId
            //var mandateOutput = (CreatePaymentUsingMandateOutputV1?)output;
            if (output != null)
                return new CreateAcceptPaymentOutput
                {
                    FlowUrl = paymentPayload.Creditor.CreditorReturnString,
                    PaymentId = output.PaymentId
                };
            return new CreateAcceptPaymentOutput()
            {
                FlowUrl = paymentPayload.Creditor.CreditorReturnString,
                PaymentId = Guid.Empty
            };
        }

        return new CreateAcceptPaymentOutput()
        {
            FlowUrl = paymentPayload.Creditor.CreditorReturnString,
            PaymentId = Guid.Empty
        };
    }

    public async Task<PaymentRequestStatusRetrievalAck> GetPaymentStatus(string lifecycleId,
        NewPaymentRequestStatusRetrieval model)
    {
        var paymentEntity = await GetPaymentFromCache(lifecycleId);
        if (paymentEntity == null)
            throw AdapterException.BadInputCode("Payment not found for lifecycleId: " + lifecycleId);
        var payment = paymentEntity.ToViewModel();
        var sacazPaymentId = Guid.Empty;
        Guid.TryParse(payment.SacAzPaymentId, out sacazPaymentId);

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = sacazPaymentId,
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = model.PaymentRequestStatusRetrievalLifecycleId
        };

        var newMessageId = Guid.NewGuid().ToString().Replace("-", "");

        var output = new PaymentRequestStatusRetrievalAck
        {
            MessageId = newMessageId,
            OriginalMessageId = model.MessageId,
            InitiatingPartyId = _configuration["PBBA_DSP_Id"],
            CreationDateTime = DateTime.UtcNow
        };
        _ = GetPaymentStatusWebhook(input, paymentEntity);

        return output;
    }

    public async Task<(object result, int statusCode)> GetPaymentStatusWebhook(WebhookRequest webhookRequest,
        PaymentStorageEntity? paymentEntity = null)
    {
        var paymentData = (AcceptPaymentStatusUpdated)webhookRequest.Data;

        var isWebhookHitFromStatusRetrieval =
            !string.IsNullOrEmpty(webhookRequest.PaymentRequestStatusRetrievalLifecycleId);

        var header = new RequestHeader
        {
            HeaderRequestId = Guid.NewGuid().ToString(),
            HeaderParticipantId = _configuration["PBBA_DSP_Id"]!,
            HeaderProductId = _configuration["PBBA_ProductId"]!,
            HeaderIdempotencyKey = Guid.NewGuid().ToString()
        };

        if (paymentEntity == null)
            //Done_TO_DO:Get data from blob using tags using paymentId
            paymentEntity = await GetPaymentBySacAzPaymentId(paymentData.PaymentId.ToString());

        var paymentDb = paymentEntity.ToViewModel();

        var dbData = await _paymentPayloadService.GetPaymentPayloadFromDb(paymentDb.ZappPaymentId);
        // var dbData = await _paymentPayloadService.GetPaymentPayloadFromDb(paymentEntity.ZappPaymentId); // FOR DEBUG

        // Check for earlier Payment Confirmation Advice Sent 
        var pymtCnfAdvSentAlready =
            await SendPaymentConfirmationAdviceFromStorage(dbData.Transaction.PaymentRequestLifecycleId);
        if (pymtCnfAdvSentAlready) return (paymentData.PaymentId, 200);

        var merchant = await _merchantService.GetMerchantFromDb(paymentEntity.ZappDistributorId);

        //Done_TO_DO:To get the token from keyVault
        var bearerToken = HelperClass.GetJwtForDistributor(_configuration, merchant.TenantId);
        // var bearerToken = HelperClass.GetJwtForDistributor(_configuration, "000645"); // FOR DEBUG

        var input = new NewPaymentConfirmationAdvice
        {
            InitiatingPartyId = _configuration["PBBA_DSP_Id"], // dbData.InitiatingPartyId,
            MessageId = dbData.MessageId,
            CreationDateTime = DateTime.UtcNow, //dbData.CreationDateTime,
            BusinessType = dbData.BusinessType,
            PaymentRequestLifecycleId = dbData.Transaction.PaymentRequestLifecycleId,
            PaymentRequestStatusRetrievalLifecycleId = webhookRequest.PaymentRequestStatusRetrievalLifecycleId,
            AcceptanceDateTime = DateTime.UtcNow,
            Status = null,
            Debtor = new Debtor
            {
                DebtorId = dbData.Debtor.DebtorId,
                DebtorServiceProviderId = _configuration["PBBA_Debtor_Service_Id"]
            }
        };

        // if isWebhookHitFromStatusRetrieval == true and PaymentId is Guid.Empty then return RJCT / SYSM
        if (isWebhookHitFromStatusRetrieval && paymentData.PaymentId == Guid.Empty)
        {
            input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.SYSM);
            await CallZappWebhookAndSaveResponse(paymentEntity,
                dbData.Transaction.PaymentRequestLifecycleId,
                input,
                header);

            return (paymentData.PaymentId, 200);
        }

        try
        {
            var paymentStatus =
                await _sacazStorageClient.PaymentService.GetAcceptPayment(bearerToken,
                    paymentData.PaymentId);
            if (paymentStatus?.Status?.Code == PaymentStatusCode.PaymentExecutedCredited ||
                paymentStatus?.Status?.Code == PaymentStatusCode.PaymentExecutedDebited)
            {
                //Done_TO_DO:get data from Db and paymentStatus to fill the pca values. as of now only hardcode value are used here 
                var acceptanceDateTime =
                    paymentStatus.Status.Events.FirstOrDefault(e => e.Event == "PROVIDER_PROCESSING")?.Timestamp ??
                    paymentStatus.Status.Events.FirstOrDefault(e => e.Event == "PENDING")?.Timestamp ??
                    paymentStatus.Status.Events.FirstOrDefault(e => e.Event == "READY_FOR_AUTHORIZE")?.Timestamp ??
                    DateTimeOffset.UtcNow;

                var paymentDateTime =
                    paymentStatus.Status.Events.FirstOrDefault(e => e.Event == "PENDING")?.Timestamp ??
                    paymentStatus.Status.Events.FirstOrDefault(e => e.Event == "PAYMENT_EXECUTED_DEBITED")?.Timestamp ??
                    paymentStatus.Status.Events.FirstOrDefault(e => e.Event == "PREPARING")?.Timestamp ??
                    DateTimeOffset.UtcNow;

                input.AcceptanceDateTime = acceptanceDateTime.DateTime.ToUniversalTime();
                input.Status = SetTransactionStatus(TransactionStatus.APPR);
                input.Payment = new PbbaApiClient.Dsp2Mastercard.Payment
                {
                    PaymentReference = paymentStatus.ProviderPaymentId == null
                        ? Guid.NewGuid().ToString()
                        : paymentStatus.ProviderPaymentId, //paymentStatus.ProviderPaymentId,
                    ClearingSystem = ClearingSystem.FPS.ToString(), //Fixed,
                    PaymentDateTime = paymentDateTime.DateTime.ToUniversalTime(),
                    PaymentAmount = new Amount
                    {
                        Currency = paymentStatus.Currency,
                        Value = Convert.ToDouble(paymentStatus.Amount)
                    }
                };
            }
            else if (paymentStatus?.Status?.Code == PaymentStatusCode.Cancelled)
            {
                input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.RJCT);
            }
            else if (paymentStatus?.Status?.Code == PaymentStatusCode.AuthorizationFlowIncomplete)
            {
                input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.ISST);
            }
            else if (paymentStatus?.Status?.Code == PaymentStatusCode.Failed)
            {
                input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.SYSP);
            }
        }
        catch (SacAzStorageClientException ex)
        {
            if (ex.StatusCode == 401)
            {
                input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.SYSM);
            }
            else if (ex.StatusCode == 400)
            {
                input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.SYSP);
            }
            else
            {
                _logger.LogError(
                    "Error while _sacazStorageClient.PaymentService.GetAcceptPayment in Webhook call - {ExcMessage}",
                    ex.Message);
                throw;
            }
        }

        // If webhook got hit from payment status retrieval,
        // Then we will always give payment confirmation advise, it could be any status or no status
        if (isWebhookHitFromStatusRetrieval && input.Status == null)
            input.Status = SetTransactionStatus(TransactionStatus.RJCT, TransactionStatusReason.RJCT);

        if (input.Status != null)
            await CallZappWebhookAndSaveResponse(paymentEntity, dbData.Transaction.PaymentRequestLifecycleId, input,
                header);

        return (paymentData.PaymentId, 200);
    }

    [ExcludeFromCodeCoverage]
    public async Task<(object result, int statusCode)> PaymentConfirmationAdvices(string PAYMENT_LIFE_CYCLE_ID,
        NewPaymentConfirmationAdvice pca, RequestHeader header)
    {
        try
        {
            var zappResponse = await _zappClient.CallZappEndpoint(PAYMENT_LIFE_CYCLE_ID, pca, header);
            return zappResponse;
        }
        catch (Exception e)
        {
            throw e;
        }
    }

    /// <summary>
    ///     Fetch Payment from Cache by lifeCycleId
    /// </summary>
    /// <param name="lifeCycleId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public async Task<PaymentStorageEntity?> GetPaymentFromCache(string lifeCycleId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"Payment_{lifeCycleId}";
        var cachePayment = _concurrentMemoryCache.Get<PaymentStorageEntity>(cacheKey);
        if (cachePayment == null)
            cachePayment = await _concurrentMemoryCache.GetOrCreateAsync(cacheKey,
                async entry => await GetPaymentFromDb(lifeCycleId));

        return cachePayment;
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool> AddDistributor(Merchant entity,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var merchant =
                await _merchantService.SaveMerchantInBlobAndCache(entity.CreditorId,
                    entity.CreditorServiceProviderId, entity.DestinationId, entity.ParticipantId, entity.TenantId,
                    cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return false;
        }

        return false;
    }

    private Status SetTransactionStatus(TransactionStatus transactionStatus, TransactionStatusReason? reason = null)
    {
        var status = new Status
        {
            TransactionStatus = transactionStatus.ToString()
        };
        if (reason != null) status.TransactionStatusReason = reason.ToString();
        return status;
    }

    private async Task CallZappWebhookAndSaveResponse(PaymentStorageEntity payment,
        string transactionPaymentRequestLifecycleId, NewPaymentConfirmationAdvice input, RequestHeader header)
    {
        try
        {
            var zappResponse =
                await _zappClient.CallZappEndpoint(transactionPaymentRequestLifecycleId, input, header);
            // FOR DEBUG comment below code
            if (zappResponse.statusCode == 200)
            {
                payment.PaymentConfirmAdviseSent = true;
                payment.PaymentConfirmAdviseSentOn = DateTimeOffset.UtcNow;

                await _paymentStorageService.Update(payment);
            }

            // Store the Request & Response in Blob == Task 54445: Store the response of payment confirmation advise in blob
            var pymtCnfAdvsEntity = new PaymentCnfAdviseResponseStorageEntity
            {
                Id = transactionPaymentRequestLifecycleId,
                RequestPayload = input,
                RequestHeaders = header
            };
            if (zappResponse.statusCode == 200)
                pymtCnfAdvsEntity.SuccessResponsePayload = (PaymentConfirmationAdvicesResponse)zappResponse.result;
            else
                pymtCnfAdvsEntity.ErrorResponsePayload = (ErrorResponse)zappResponse.result;

            await _pymtResponseStorageService.Create(pymtCnfAdvsEntity);
        }
        catch (Exception e)
        {
            _logger.LogError(
                $"Exception in CallZappWebhookAndSaveResponse : Message: {e.Message}, Stack trace: {e.StackTrace}");
            throw;
        }
    }

    private async Task<bool> SendPaymentConfirmationAdviceFromStorage(string paymentRequestLifecycleId)
    {
        var isPymtCnfAdvSent = false;
        var getPymtCnfAdvsEntity = await _pymtResponseStorageService.Get(paymentRequestLifecycleId);
        if (getPymtCnfAdvsEntity.IsSuccess)
        {
            isPymtCnfAdvSent = true;
            var pymtCnfAdvsEntity = getPymtCnfAdvsEntity.Value;
            await _zappClient.CallZappEndpoint(paymentRequestLifecycleId, pymtCnfAdvsEntity.RequestPayload,
                pymtCnfAdvsEntity.RequestHeaders);
        }

        return isPymtCnfAdvSent;
    }

    public static long UnixTimestampFromDateTime(DateTime dateTime)
    {
        return (long)(dateTime - DateTime.UnixEpoch).TotalSeconds;
    }

    private void AddHeaders(HttpRequestHeaders headers, IHeaderDictionary headerDict)
    {
        foreach (var kvp in headerDict)
            if (!headers.TryGetValues(kvp.Key, out _))
                headers.Add(kvp.Key, kvp.Value.ToString());
    }

    /// <summary>
    ///     First time store Payment to Blob with empty SacAzPaymentId and empty SacAzPaymentUrl
    /// </summary>
    /// <param name="model"></param>
    /// <param name="merchant"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task<Payment> SavePaymentToStorageFirstTime(NewPaymentRequestWithAgreement model, Merchant merchant,
        CancellationToken cancellationToken)
    {
        var paymentEntity = new PaymentStorageEntity
        {
            Id = model.Transaction.PaymentRequestLifecycleId,
            DestinationId = merchant.DestinationId,
            ZappMerchantId = merchant.TenantId,
            ZappDistributorId = merchant.CreditorServiceProviderId,
            SacAzPaymentId = "", // Saving empty for First time and will be updated later
            SacAzPaymentUrl = "", // Saving empty for First time and will be updated later
            ZappPaymentId = model.Transaction.PaymentRequestLifecycleId,
            MerchantReturnUrl = model.Creditor.CreditorReturnString,
            PaymentConfirmAdviseSent = false
        };
        var result = await _paymentStorageService.Create(paymentEntity, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            return result.Value.ToViewModel();
        if (result.Error == StorageOperationErrorType.Conflict)
            throw new Exception("Some conflicts happened in getting merchant");

        return null;
    }


    /// <summary>
    ///     Fetch Payment from Blob Data Storage by zappPaymentId
    /// </summary>
    /// <param name="zappPaymentId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task<PaymentStorageEntity?> GetPaymentFromDb(string zappPaymentId,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentStorageService.Get(zappPaymentId, cancellationToken);
        if (result.IsSuccess)
        {
            return result.Value;
        }

        return null;
    }

    /// <summary>
    ///     To get the zappPaymentId from payment Db
    /// </summary>
    /// <param name="lifecycleId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task<PaymentStorageEntity> GetZappPaymentId(string lifecycleId,
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Convert.ToDouble(_configuration["GetPaymentBlobTimeOutSec"]));
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                var result = await _paymentStorageService.Get(lifecycleId, cancellationToken);
                if (result.IsSuccess)
                    return result.Value;
                //Sleep for a short interval before next attempt
                await Task.Delay(100);
            }
            catch (Exception e)
            {
                throw;
            }
        }

        throw AdapterException.BadInputCode(
            "LifeCycle Id passed is not valid or Payment not found with the provided lifecycle id.");
    }

    private async Task<PaymentStorageEntity> GetPaymentBySacAzPaymentId(string SacAzPaymentId)
    {
        var tags =
            new Dictionary<string, string>
            {
                { "SacAzPaymentId", SacAzPaymentId }
            };
        var result = await _paymentStorageService.SearchByTags(tags);
        if (result.IsSuccess)
        {
            var paymentEntity = result.Value;
            // old === return paymentEntity.ToViewModel(); === code
            return paymentEntity;
        }

        if (result.Error == StorageOperationErrorType.NotFound)
            throw AdapterException.BadInputCode("Payment with SacAzPaymentId is not found.");

        return null;
    }

    public static RSA LoadPrivateKey(string filePath)
    {
        try
        {
            return ReadPrivateKey(File.ReadAllText(filePath));
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }

        return null;
    }

    private static RSA ReadPrivateKey(string key)
    {
        var privateKeyPEM = key
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\r", "")
            .Replace("\n", "");

        var encoded = Convert.FromBase64String(privateKeyPEM);

        var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(encoded, out _);
        return rsa;
    }

    //It will be removed once we have updated SacAz Storage Client Library
    private async Task<CreatePaymentUsingMandateOutput> CreateMandateUsingPayment(
        string url, Guid MandateId,
        string token,
        CreatePaymentUsingMandateInput input)
    {
        var fullUrl = $"{url}/{MandateId}/payments";
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            var jsonPayload = JsonConvert.SerializeObject(input);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var responseMessage = await client.PostAsync(fullUrl, content);
            if (responseMessage.IsSuccessStatusCode)
            {
                var responseContent = await responseMessage.Content.ReadAsStringAsync();
                var responseData = JsonConvert.DeserializeObject<CreatePaymentUsingMandateOutput>(responseContent);
                return responseData;
            }

            return null;
        }
    }
}