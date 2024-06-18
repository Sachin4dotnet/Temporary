using System.Net;
using System.Text.Json;
using SachinAdapter.AzureServices.Storage;
using SachinAdapter.AzureTables;
using SachinAdapter.Filters.Exceptions;
using SachinAdapter.Models.AzureTables;
using SachinAdapter.Models.AzureTables.StorageEntity;
using SachinAdapter.Models.Enums;
using SachinAdapter.Models.Error;
using SachinAdapter.Models.Requests;
using SachinAdapter.Models.Requests.Payments;
using SachinAdapter.Models.Response;
using SachinAdapter.Services;
using SachinAdapter.Utilities;
using SacAz.Storage.Client;
using SacAz.Storage.Models.Payments;
using SacAz.Storage.Models.Payments.Inputs;
using SacAz.Storage.Models.Payments.Outputs;
using AutoMapper;
using CSharpFunctionalExtensions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using PbbaApiClient.Dsp2Mastercard;
using PbbaApiClient.Mastercard2Dsp;
using AgreementStatus = SachinAdapter.Models.Response.AgreementStatus;
using Amount = PbbaApiClient.Mastercard2Dsp.Amount;
using CreditorAccount = PbbaApiClient.Mastercard2Dsp.CreditorAccount;
using Debtor = PbbaApiClient.Mastercard2Dsp.Debtor;
using ErrorResponse = SachinAdapter.Models.Response.ErrorResponse;
using RequestStatus = SachinAdapter.Models.Response.RequestStatus;

namespace SachinAdapter.Test.Services;

public class PaymentServiceTest
{
    private readonly Mock<IAgreementService> _mockAgreementService;
    private readonly Mock<ISacAzStorageClient> _mockSacAzStorageClient;
    private readonly Mock<IConcurrentMemoryCache> _mockConcurrentMemoryCache;
    private readonly Mock<ILogger<PaymentService>> _mockLogger;
    private readonly Mock<IMapper> _mockMapper;
    private readonly Mock<IMerchantService> _mockMerchantService;
    private readonly Mock<IStorageService<PaymentCnfAdviseResponseStorageEntity>> _mockPaymentCnfResponseStorageService;
    private readonly Mock<IPaymentPayloadService> _mockPaymentPayloadService;
    private readonly Mock<IStorageService<PaymentStorageEntity>> _mockPaymentStorageService;
    private readonly Mock<IZappClient> _mockZappClient;
    private readonly IConfiguration configuration;
    private PaymentService paymentService;

    public PaymentServiceTest()
    {
        _mockSacAzStorageClient = new Mock<ISacAzStorageClient>(MockBehavior.Strict);
        _mockConcurrentMemoryCache = new Mock<IConcurrentMemoryCache>(MockBehavior.Strict);
        _mockMerchantService = new Mock<IMerchantService>(MockBehavior.Strict);
        _mockPaymentPayloadService = new Mock<IPaymentPayloadService>(MockBehavior.Strict);
        _mockPaymentStorageService = new Mock<IStorageService<PaymentStorageEntity>>(MockBehavior.Strict);
        _mockPaymentCnfResponseStorageService =
            new Mock<IStorageService<PaymentCnfAdviseResponseStorageEntity>>(MockBehavior.Strict);
        _mockZappClient = new Mock<IZappClient>(MockBehavior.Strict);
        _mockAgreementService = new Mock<IAgreementService>(MockBehavior.Strict);
        _mockLogger = new Mock<ILogger<PaymentService>>(MockBehavior.Strict);

        var inMemorySettings = new Dictionary<string, string>
        {
            { "TopLevelKey", "TopLevelValue" },
            { "keyVaultZappAdapterTenantId", "000645" },
            { "KeySecrets:Keys:0:TenantId", "b1aecdd6-73c8-45f6-8789-9e162532a63f" },
            {
                "KeySecrets:Keys:0:Token",
                "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkYjhjYWM4MS01Y2RhLTQzMWUtYmNiOC01NmFkNTI4OTM2ZWMiLCJpYXQiOjE2OTYyMzkwMjIsImV4cCI6MTcxNzM0OTAyMn0.IN0jU5adPW2JFlrB_r-8muUBDjuWXmyBMElXOhDF6So"
            },
            { "GetPaymentBlobTimeOutSec", "10" },
            { "PaymentUri", "https://SacAzstorage-payments.test.SacAz.eu" }
        };
        configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings)
            .Build();
        _mockMapper = new Mock<IMapper>();
        paymentService = new PaymentService(_mockSacAzStorageClient.Object,
            configuration, _mockMapper.Object,
            _mockConcurrentMemoryCache.Object,
            _mockMerchantService.Object, _mockPaymentPayloadService.Object,
            _mockPaymentStorageService.Object,
            _mockPaymentCnfResponseStorageService.Object,
            _mockZappClient.Object, _mockAgreementService.Object,
            _mockLogger.Object);
    }

    [SetUp]
    public void Setup()
    {
        paymentService = new PaymentService(_mockSacAzStorageClient.Object,
            configuration, _mockMapper.Object,
            _mockConcurrentMemoryCache.Object,
            _mockMerchantService.Object, _mockPaymentPayloadService.Object,
            _mockPaymentStorageService.Object,
            _mockPaymentCnfResponseStorageService.Object,
            _mockZappClient.Object, _mockAgreementService.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task InitiatePaymentRequest_Should_Return_Valid_Response()
    {
        //Arrange
        var zappMerchantId = Guid.NewGuid().ToString();
        var zappDistributionId = Guid.NewGuid().ToString();
        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var requestModel = new NewPaymentRequestWithAgreement
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new Transaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF.ToString(),
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT.ToString(),
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DeliveryAddress = new DeliveryAddressForPWA
                {
                    AddressLine1 = "House No 1",
                    AddressLine2 = "Amazing Street",
                    City = "London",
                    CountrySubdivision = "Cambridgeshire",
                    PostCode = "AB1 2CD",
                    Country = "GBR"
                },
                DebtorInteractionType = "INSN"
            }
        };
        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var dataHash =
            HelperClass.CreateMd5(header.HeaderParticipantId + "-" + JsonSerializer.Serialize(requestModel.Creditor));

        var responseMerchant = new Merchant
        {
            CreditorServiceProviderId = zappDistributionId,
            DestinationId = "abc123",
            ParticipantId = "1234"
        };
        var resultPayment = new PaymentStorageEntity
        {
            Id = requestModel.Transaction.PaymentRequestLifecycleId,
            ZappDistributorId = zappDistributionId,
            ZappMerchantId = zappMerchantId,
            DestinationId = "abc1231",
            SacAzPaymentUrl = "",
            SacAzPaymentId = "",
            ZappPaymentId = requestModel.Transaction.PaymentRequestLifecycleId
        };
        var serviceResult2 = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(resultPayment);

        _mockMapper.Setup(m => m.Map<PaymentPayload>(It.IsAny<NewPaymentRequestWithAgreement>()))
            .Returns(payloadModel);
        _mockMerchantService.Setup(m => m.GetMerchant(It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseMerchant);
        _mockPaymentPayloadService.Setup(m => m.CreatePaymentPayloadToDb(
                It.IsAny<PaymentPayloadStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockPaymentStorageService.Setup(m => m.Create(
                It.IsAny<PaymentStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(serviceResult2);

        //Act
        var response = await paymentService.InitiatePaymentRequest(requestModel, header);

        //Assert
        response.Should().NotBeNull()
            .And.BeOfType<MessageResponseBlock>();
    }

    [Test]
    public async Task CreatePayment_SuccessfullyCreatesPayment_MCOBS()
    {
        //Arrange
        var lifecycleid = "12345";
        var providerId = "GB_TestBank";

        var expectedOutput = new CreateAcceptPaymentOutput
        {
            PaymentId = Guid.Parse("e6a6f24d-bb25-4890-8ba8-eca4a2832df1"),
            FlowUrl =
                "https://connectplatform.test.SacAz.eu/start/storage-payments:5bc449aa-e396-48c1-9cb6-f9b4268d178f"
        };

        var payment = new PaymentStorageEntity
        {
            ZappPaymentId = lifecycleid,
            DestinationId = "f446f245-4e30-4e15-a349-77e48ed4c9b9",
            ZappDistributorId = "000645"
        };

        var paymentPayload = new PaymentPayloadStorageEntity
        {
            Id = payment.ZappPaymentId,
            RequestPayload = new PaymentPayload
            {
                InitiatingPartyId = "000545",
                MessageId = "7eab4eab35a542e085add0363a49c035",
                CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                BusinessType = 3,
                Debtor = new DebtorForPWA
                {
                    DebtorId = "Fxrsi5345",
                    DebtorServiceProviderId = "000369"
                },
                Creditor = new CreditorForPWA
                {
                    CreditorId = "VerizonoMobile000588",
                    CreditorServiceProviderId = "000645",
                    CreditorCategoryCode = "0742",
                    CreditorTradeName = "VerizonoMobileLtd",
                    CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                    CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                    CreditorAccount = new CreditorAccount
                    {
                        AccountNumber = "45122351223323",
                        ClearingSystem = ClearingSystem.FPS.ToString(),
                        AccountType = "PERS",
                        AccountName = "Verizono"
                    },
                    CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
                },
                Transaction = new PaymentPayloadTransaction
                {
                    AgreementId = "MCOBS",
                    AgreementType = AgreementType.AOF,
                    PaymentRequestLifecycleId = "923123123123123100",
                    EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                    InstructionId = "98dj4eab35a542e085add0363a40jk564",
                    ConfirmationExpiryTimeInterval = 150,
                    PaymentRequestType = PaymentRequestType.IMDT,
                    TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                    InstructedAmount = new Amount
                    {
                        Currency = Currency.GBP.ToString(),
                        Value = 100.25
                    },
                    Purpose = "ONLN",
                    CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                    Restriction = "18PLUS",
                    StrongCustomerAuthentication = true,
                    DebtorInteractionType = "INSN"
                }
            }
        };

        var merchant = new Merchant()
        {
            DestinationId = "2cb0d564-10dc-4842-86e1-1439e3efba0a",
            CreditorServiceProviderId = "000358",
            CreditorId = "PBBApb9P07Y25ae4pS8Al4Tz",
            ParticipantId = "942778e1-ef0a-42fc-8e40-27c976e98818",
            TenantId = "b1aecdd6-73c8-45f6-8789-9e162532a63f"
        };


        _mockPaymentStorageService.Setup(service => service.Get(It.IsAny<string>(), default))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        _mockPaymentPayloadService.Setup(service => service.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(paymentPayload.RequestPayload);
        _mockMerchantService.Setup(service => service.GetMerchantFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(merchant);

        _mockSacAzStorageClient.Setup(client =>
            client.PaymentService.CreateAcceptPayment(It.IsAny<string>(), It.IsAny<CreateAcceptPaymentInput>(),
                default)).ReturnsAsync(expectedOutput);

        _mockPaymentStorageService.Setup(service => service.Update(It.IsAny<PaymentStorageEntity>(),
                default, StorageLockType.LastWriterWins))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        var result = await paymentService.CreatePayment(lifecycleid, providerId);

        Assert.NotNull(result);
        Assert.AreEqual(expectedOutput.PaymentId, result.PaymentId);
        Assert.AreEqual(expectedOutput.FlowUrl, result.FlowUrl);
    }

/*
    [Test]
    public async Task CreatePayment_Should_ResponseFromCache()
    {
        //Arrange
        var lifecycleid = "12345";
        var providerId = "GB_TestBank";

        var expectedOutput = new CreateAcceptPaymentOutput
        {
            PaymentId = Guid.Parse("e6a6f24d-bb25-4890-8ba8-eca4a2832df1"),
            FlowUrl =
                "https://connectplatform.test.SacAz.eu/start/storage-payments:5bc449aa-e396-48c1-9cb6-f9b4268d178f"
        };

        var payment = new PaymentStorageEntity
        {
            ZappPaymentId = lifecycleid,
            DestinationId = "f446f245-4e30-4e15-a349-77e48ed4c9b9",
            ZappDistributorId = "000645",
            SacAzPaymentId = expectedOutput.PaymentId.ToString(),
            SacAzPaymentUrl = expectedOutput.FlowUrl,
            Created = DateTimeOffset.Now.AddSeconds(-100)
        };

        var paymentPayload = new PaymentPayloadStorageEntity
        {
            Id = payment.ZappPaymentId,
            RequestPayload = new PaymentPayload
            {
                InitiatingPartyId = "000545",
                MessageId = "7eab4eab35a542e085add0363a49c035",
                CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                BusinessType = 3,
                Debtor = new DebtorForPWA
                {
                    DebtorId = "Fxrsi5345",
                    DebtorServiceProviderId = "000369"
                },
                Creditor = new CreditorForPWA
                {
                    CreditorId = "VerizonoMobile000588",
                    CreditorServiceProviderId = "000645",
                    CreditorCategoryCode = "0742",
                    CreditorTradeName = "VerizonoMobileLtd",
                    CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                    CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                    CreditorAccount = new CreditorAccount
                    {
                        AccountNumber = "45122351223323",
                        ClearingSystem = ClearingSystem.FPS.ToString(),
                        AccountType = "PERS",
                        AccountName = "Verizono"
                    },
                    CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
                },
                Transaction = new PaymentPayloadTransaction
                {
                    AgreementId = "a3e2a749088440eab8b40c926efe2931",
                    AgreementType = AgreementType.AOF,
                    PaymentRequestLifecycleId = "923123123123123100",
                    EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                    InstructionId = "98dj4eab35a542e085add0363a40jk564",
                    ConfirmationExpiryTimeInterval = 150,
                    PaymentRequestType = PaymentRequestType.IMDT,
                    TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                    InstructedAmount = new Amount
                    {
                        Currency = Currency.GBP.ToString(),
                        Value = 100.25
                    },
                    Purpose = "ONLN",
                    CategoryPurpose = CategoryPurpose.PYMT,
                    Restriction = "18PLUS",
                    StrongCustomerAuthentication = true,
                    DebtorInteractionType = "INSN"
                }
            }
        };

        _mockPaymentStorageService.Setup(service => service.Get(It.IsAny<string>(), default))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));
        // _mockPaymentPayloadService.Setup(service => service.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
        //     .ReturnsAsync(paymentPayload.RequestPayload);
        // _mockSacAzStorageClient.Setup(client =>
        //     client.PaymentService.CreateAcceptPayment(It.IsAny<string>(), It.IsAny<CreateAcceptPaymentInput>(),
        //         default)).ReturnsAsync(expectedOutput);

        var result = await paymentService.CreatePayment(lifecycleid, providerId);

        Assert.NotNull(result);
        Assert.AreEqual(expectedOutput.PaymentId, result.PaymentId);
        Assert.AreEqual(expectedOutput.FlowUrl, result.FlowUrl);
    }
*/
    [Test]
    public async Task CreatePayment_SuccessfullyCreatesPayment_Other_Than_MCOBS()
    {
        //Arrange
        var lifecycleid = "12345";
        var providerId = "GB_TestBank";

        var payment = new PaymentStorageEntity
        {
            ZappPaymentId = lifecycleid,
            DestinationId = "f446f245-4e30-4e15-a349-77e48ed4c9b9",
            ZappDistributorId = "000645"
        };

        var paymentPayload = new PaymentPayloadStorageEntity
        {
            Id = payment.ZappPaymentId,
            RequestPayload = new PaymentPayload
            {
                InitiatingPartyId = "000545",
                MessageId = "7eab4eab35a542e085add0363a49c035",
                CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                BusinessType = 3,
                Debtor = new DebtorForPWA
                {
                    DebtorId = "Fxrsi5345",
                    DebtorServiceProviderId = "000369"
                },
                Creditor = new CreditorForPWA
                {
                    CreditorId = "VerizonoMobile000588",
                    CreditorServiceProviderId = "000645",
                    CreditorCategoryCode = "0742",
                    CreditorTradeName = "VerizonoMobileLtd",
                    CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                    CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                    CreditorAccount = new CreditorAccount
                    {
                        AccountNumber = "45122351223323",
                        ClearingSystem = ClearingSystem.FPS.ToString(),
                        AccountType = "PERS",
                        AccountName = "Verizono"
                    },
                    CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
                },
                Transaction = new PaymentPayloadTransaction
                {
                    AgreementId = "12345",
                    AgreementType = AgreementType.AOF,
                    PaymentRequestLifecycleId = "923123123123123100",
                    EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                    InstructionId = "98dj4eab35a542e085add0363a40jk564",
                    ConfirmationExpiryTimeInterval = 150,
                    PaymentRequestType = PaymentRequestType.IMDT,
                    TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                    InstructedAmount = new Amount
                    {
                        Currency = Currency.GBP.ToString(),
                        Value = 100.25
                    },
                    Purpose = "ONLN",
                    CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                    Restriction = "18PLUS",
                    StrongCustomerAuthentication = true,
                    DebtorInteractionType = "INSN"
                }
            }
        };

        var getAgreementId = new AgreementStorageEntity
        {
            ZappAgreementId = "123",
            PaymentId = "123",
            SacAzMandateId = "308e35d5-3b7f-48b0-b142-1356efdb84ed",
            ZappDistributorId = "000645",
            DestinationId = "7bdbe845-89f4-41d9-ae0b-845c1c38564c"
        };

        _mockPaymentStorageService.Setup(service => service.Get(It.IsAny<string>(), default))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        _mockAgreementService.Setup(x => x.GetAgreementById(It.IsAny<string>(), default))
            .ReturnsAsync(getAgreementId);

        _mockPaymentPayloadService.Setup(service => service.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(paymentPayload.RequestPayload);

        //Act 
        var result = await paymentService.CreatePayment(lifecycleid, providerId);

        //Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(paymentPayload.RequestPayload.Creditor.CreditorReturnString, result.FlowUrl);
        Assert.AreEqual(Guid.Empty, result.PaymentId);
    }

    [Test]
    public async Task CreatePayment_SuccessfullyCreatesPayment_Other_Than_MCOBS_MandateId_Null()
    {
        //Arrange
        var lifecycleid = "12345";
        var providerId = "GB_TestBank";

        var payment = new PaymentStorageEntity
        {
            ZappPaymentId = lifecycleid,
            DestinationId = "f446f245-4e30-4e15-a349-77e48ed4c9b9",
            ZappDistributorId = "000645"
        };

        var paymentPayload = new PaymentPayloadStorageEntity
        {
            Id = payment.ZappPaymentId,
            RequestPayload = new PaymentPayload
            {
                InitiatingPartyId = "000545",
                MessageId = "7eab4eab35a542e085add0363a49c035",
                CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                BusinessType = 3,
                Debtor = new DebtorForPWA
                {
                    DebtorId = "Fxrsi5345",
                    DebtorServiceProviderId = "000369"
                },
                Creditor = new CreditorForPWA
                {
                    CreditorId = "VerizonoMobile000588",
                    CreditorServiceProviderId = "000645",
                    CreditorCategoryCode = "0742",
                    CreditorTradeName = "VerizonoMobileLtd",
                    CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                    CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                    CreditorAccount = new CreditorAccount
                    {
                        AccountNumber = "45122351223323",
                        ClearingSystem = ClearingSystem.FPS.ToString(),
                        AccountType = "PERS",
                        AccountName = "Verizono"
                    },
                    CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
                },
                Transaction = new PaymentPayloadTransaction
                {
                    AgreementId = "12345",
                    AgreementType = AgreementType.AOF,
                    PaymentRequestLifecycleId = "923123123123123100",
                    EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                    InstructionId = "98dj4eab35a542e085add0363a40jk564",
                    ConfirmationExpiryTimeInterval = 150,
                    PaymentRequestType = PaymentRequestType.IMDT,
                    TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                    InstructedAmount = new Amount
                    {
                        Currency = Currency.GBP.ToString(),
                        Value = 100.25
                    },
                    Purpose = "ONLN",
                    CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                    Restriction = "18PLUS",
                    StrongCustomerAuthentication = true,
                    DebtorInteractionType = "INSN"
                }
            }
        };

        var getAgreementId = new AgreementStorageEntity
        {
            ZappAgreementId = "123",
            PaymentId = "123",
            SacAzMandateId = null,
            ZappDistributorId = "000645",
            DestinationId = "7bdbe845-89f4-41d9-ae0b-845c1c38564c"
        };

        _mockPaymentStorageService.Setup(service => service.Get(It.IsAny<string>(), default))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        _mockAgreementService.Setup(x => x.GetAgreementById(It.IsAny<string>(), default))
            .ReturnsAsync(getAgreementId);

        _mockPaymentPayloadService.Setup(service => service.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(paymentPayload.RequestPayload);

        //Act 
        var result = await paymentService.CreatePayment(lifecycleid, providerId);
        //Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(paymentPayload.RequestPayload.Creditor.CreditorReturnString, result.FlowUrl);
        Assert.AreEqual(Guid.Empty, result.PaymentId);
    }

    [Test]
    public async Task GetPaymentStatus_ShouldThrowException_WhenPaymentNotFound()
    {
        //Arrange 
        var lifeCycleId = "123";

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

        _mockConcurrentMemoryCache.Setup(m =>
            m.GetOrCreateAsync(It.IsAny<string>(),
                It.IsAny<Func<ICacheEntry, Task<PaymentStorageEntity>>>())
        ).ReturnsAsync((PaymentStorageEntity)null);
        // this way we can throw exception ).ThrowsAsync(AdapterException.BadInputCode("Payment not found for lifecycleId: 923123123123123100")); // this way we can throw exception

        //Act and Assert
        Assert.ThrowsAsync<AdapterException>(async () => await paymentService.GetPaymentStatus(lifeCycleId, input));
    }

    [Test]
    public async Task GetPaymentStatus_ShouldReturnValidResponse_WhenPaymentFound()
    {
        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
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

        _mockConcurrentMemoryCache.Setup(m =>
            m.GetOrCreateAsync(It.IsAny<string>(),
                It.IsAny<Func<ICacheEntry, Task<PaymentStorageEntity>>>())
        ).ReturnsAsync(payment);

        //Act
        var result = await paymentService.GetPaymentStatus("923123123123123101", input);

        //Assert
        Assert.NotNull(result);
        Assert.AreEqual("7eab4eab35a542e085add0363a49c035", result.OriginalMessageId);
    }

    [Test]
    public async Task GetPaymentStatusWebhook_Returns_PaymentStatus()
    {
        //Arrange
        var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var expectedPaymentStatus = new AcceptPaymentOutput
        {
            PaymentId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Status = new PaymentStatus
            {
                Code = PaymentStatusCode.Initiated,
                Events = new List<PaymentStatusChangeEvent>
                {
                    new()
                    {
                        Event = "Authorized",
                        Timestamp = DateTimeOffset.Now.DateTime
                    },
                    new()
                    {
                        Event = "Initiated",
                        Timestamp = DateTimeOffset.Now.DateTime
                    }
                }
            },
            DestinationId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Currency = Currency.GBP.ToString(),
            Amount = 100
        };

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
        };

        var expectedZappResponse = new PaymentConfirmationAdvicesResponse
        {
            initiatingPartyId = "123",
            messageId = "123",
            creationDateTime = DateTime.Now,
            originalMessageId = "123",
            paymentRequestLifecycleId = "123",
            requestStatus = new RequestStatus { paymentRequestStatus = "APPR" },
            agreementStatus = new AgreementStatus { agreementId = "123", agreementStatus = "APRD" }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var successResult = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment);

        var successResultPymtCnfAdvSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());
        var failureResultPymtCnfAdvResponse =
            Result.Failure<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(StorageOperationErrorType
                .NotFound);

        _mockPaymentStorageService.Setup(x => x.SearchByTags(It.IsAny<Dictionary<string, string>>(), default))
            .ReturnsAsync(successResult);

        _mockSacAzStorageClient
            .Setup(x => x.PaymentService.GetAcceptPayment(It.IsAny<string>(), It.IsAny<Guid>(), default))
            .ReturnsAsync(expectedPaymentStatus);

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Get(It.Is<string>(t => t == payloadModel.Transaction.PaymentRequestLifecycleId),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResultPymtCnfAdvResponse);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.IsAny<NewPaymentConfirmationAdvice>(), It.IsAny<RequestHeader>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedZappResponse, 200)));

        _mockPaymentCnfResponseStorageService.Setup(_ =>
                _.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultPymtCnfAdvSaving);

        _mockPaymentStorageService.Setup(service => service.Update(It.IsAny<PaymentStorageEntity>(),
                default, StorageLockType.LastWriterWins))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        //Act
        var result = await paymentService.GetPaymentStatusWebhook(input);

        //Assert
        Assert.NotNull(result);
    }

    [Test]
    public async Task GetPaymentStatusWebhook_Returns_PaymentStatus_WithErrorFromZapp()
    {
        //Arrange
        var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var expectedPaymentStatus = new AcceptPaymentOutput
        {
            PaymentId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Status = new PaymentStatus
            {
                Code = PaymentStatusCode.Initiated,
                Events = new List<PaymentStatusChangeEvent>
                {
                    new()
                    {
                        Event = "Authorized",
                        Timestamp = DateTimeOffset.Now.DateTime
                    },
                    new()
                    {
                        Event = "Initiated",
                        Timestamp = DateTimeOffset.Now.DateTime
                    }
                }
            },
            DestinationId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Currency = Currency.GBP.ToString(),
            Amount = 100
        };

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
        };

        var expectedZappResponse = new ErrorResponse
        {
            Errors = new ErrorInfo
            {
                ErrorList = new List<ErrorDetail>
                {
                    new()
                    {
                        Description = "Error Description",
                        ReasonCode = "ABCD",
                        Source = "Error",
                        Recoverable = false
                    }
                }
            }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var merchant = new Merchant
        {
            DestinationId = "2cb0d564-10dc-4842-86e1-1439e3efba0a",
            CreditorServiceProviderId = "000654",
            CreditorId = "PBBApb9P07Y25ae4pS8Al4Tz",
            ParticipantId = "942778e1-ef0a-42fc-8e40-27c976e98818",
            TenantId = "b1aecdd6-73c8-45f6-8789-9e162532a63f"
        };

        var successResult = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment);

        var successResultResponseSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());
        var failureResultPymtCnfAdvResponse =
            Result.Failure<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(StorageOperationErrorType
                .NotFound);

        _mockPaymentStorageService.Setup(x => x.SearchByTags(It.IsAny<Dictionary<string, string>>(), default))
            .ReturnsAsync(successResult);

        _mockSacAzStorageClient
            .Setup(x => x.PaymentService.GetAcceptPayment(It.IsAny<string>(), It.IsAny<Guid>(), default))
            .ReturnsAsync(expectedPaymentStatus);

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Get(It.Is<string>(t => t == payloadModel.Transaction.PaymentRequestLifecycleId),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResultPymtCnfAdvResponse);

        _mockMerchantService.Setup(service => service.GetMerchantFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(merchant);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.IsAny<NewPaymentConfirmationAdvice>(), It.IsAny<RequestHeader>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedZappResponse, 400)));
        _mockPaymentCnfResponseStorageService.Setup(_ =>
                _.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultResponseSaving);

        //Act
        var result = await paymentService.GetPaymentStatusWebhook(input);

        //Assert
        Assert.NotNull(result);
    }

    [TestCase(PaymentStatusCode.PaymentExecutedDebited, TransactionStatus.APPR)]
    [TestCase(PaymentStatusCode.PaymentExecutedCredited, TransactionStatus.APPR)]
    [TestCase(PaymentStatusCode.Cancelled, TransactionStatus.RJCT)]
    [TestCase(PaymentStatusCode.Failed, TransactionStatus.RJCT)]
    [TestCase(PaymentStatusCode.AuthorizationFlowIncomplete, TransactionStatus.RJCT)]
    public async Task GetPaymentStatusWebhook_Returns_DifferentPaymentStatus(PaymentStatusCode paymentStatus,
        TransactionStatus transactionStatus)
    {
        //Arrange
        var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var expectedPaymentStatus = new AcceptPaymentOutput
        {
            PaymentId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Status = new PaymentStatus
            {
                Code = paymentStatus,
                Events = new List<PaymentStatusChangeEvent>
                {
                    new()
                    {
                        Event = "Authorized",
                        Timestamp = DateTimeOffset.Now.DateTime
                    },
                    new()
                    {
                        Event = "Initiated",
                        Timestamp = DateTimeOffset.Now.DateTime
                    }
                }
            },
            DestinationId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Currency = Currency.GBP.ToString(),
            Amount = 100
        };

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
        };

        var expectedZappResponse = new ErrorResponse
        {
            Errors = new ErrorInfo
            {
                ErrorList = new List<ErrorDetail>
                {
                    new()
                    {
                        Description = "Error Description",
                        ReasonCode = "ABCD",
                        Source = "Error",
                        Recoverable = false
                    }
                }
            }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var successResult = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment);

        var successResultResponseSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());

        var failureResultPymtCnfAdvResponse =
            Result.Failure<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(StorageOperationErrorType
                .NotFound);

        _mockPaymentStorageService.Setup(x => x.SearchByTags(It.IsAny<Dictionary<string, string>>(), default))
            .ReturnsAsync(successResult);

        _mockSacAzStorageClient
            .Setup(x => x.PaymentService.GetAcceptPayment(It.IsAny<string>(), It.IsAny<Guid>(), default))
            .ReturnsAsync(expectedPaymentStatus);

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Get(It.Is<string>(t => t == payloadModel.Transaction.PaymentRequestLifecycleId),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResultPymtCnfAdvResponse);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.Is<NewPaymentConfirmationAdvice>(t =>
                        t.Status.TransactionStatus == transactionStatus.ToString()),
                    It.IsAny<RequestHeader>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedZappResponse, 400)));
        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultResponseSaving);

        //Act
        var result = await paymentService.GetPaymentStatusWebhook(input);

        //Assert
        Assert.NotNull(result);
    }


    [TestCase(HttpStatusCode.BadRequest, TransactionStatusReason.SYSP)]
    [TestCase(HttpStatusCode.Unauthorized, TransactionStatusReason.SYSM)]
    public async Task GetPaymentStatusWebhook_Returns_DifferentPaymentStatus_ForException(HttpStatusCode statusCode,
        TransactionStatusReason transactionStatusReason)
    {
        //Arrange
        var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
        };

        var expectedZappResponse = new ErrorResponse
        {
            Errors = new ErrorInfo
            {
                ErrorList = new List<ErrorDetail>
                {
                    new()
                    {
                        Description = "Error Description",
                        ReasonCode = "ABCD",
                        Source = "Error",
                        Recoverable = false
                    }
                }
            }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var successResult = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment);

        var successResultResponseSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());

        _mockPaymentStorageService.Setup(x => x.SearchByTags(It.IsAny<Dictionary<string, string>>(), default))
            .ReturnsAsync(successResult);

        _mockSacAzStorageClient
            .Setup(x => x.PaymentService.GetAcceptPayment(It.IsAny<string>(), It.IsAny<Guid>(), default))
            .ThrowsAsync(new SacAzStorageClientException("Any error message", (int)statusCode, "response",
                new Exception("error message")));

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.Is<NewPaymentConfirmationAdvice>(t =>
                        t.Status.TransactionStatus == TransactionStatus.RJCT.ToString()
                        && t.Status.TransactionStatusReason == transactionStatusReason.ToString()),
                    It.IsAny<RequestHeader>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedZappResponse, 400)));

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultResponseSaving);

        //Act
        var result = await paymentService.GetPaymentStatusWebhook(input);

        //Assert
        Assert.NotNull(result);
    }

    [Test]
    public async Task GetPaymentStatusWebhook_Throws_Exception_fromCallZappEndpoint()
    {
        //Arrange
        var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var expectedPaymentStatus = new AcceptPaymentOutput
        {
            PaymentId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Status = new PaymentStatus
            {
                Code = PaymentStatusCode.Initiated,
                Events = new List<PaymentStatusChangeEvent>
                {
                    new()
                    {
                        Event = "Authorized",
                        Timestamp = DateTimeOffset.Now.DateTime
                    },
                    new()
                    {
                        Event = "Initiated",
                        Timestamp = DateTimeOffset.Now.DateTime
                    }
                }
            },
            DestinationId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Currency = Currency.GBP.ToString(),
            Amount = 100
        };

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
        };

        var expectedZappResponse = new ErrorResponse
        {
            Errors = new ErrorInfo
            {
                ErrorList = new List<ErrorDetail>
                {
                    new()
                    {
                        Description = "Error Description",
                        ReasonCode = "ABCD",
                        Source = "Error",
                        Recoverable = false
                    }
                }
            }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var merchant = new Merchant
        {
            DestinationId = "2cb0d564-10dc-4842-86e1-1439e3efba0a",
            CreditorServiceProviderId = "000654",
            CreditorId = "PBBApb9P07Y25ae4pS8Al4Tz",
            ParticipantId = "942778e1-ef0a-42fc-8e40-27c976e98818",
            TenantId = "b1aecdd6-73c8-45f6-8789-9e162532a63f"
        };

        var successResult = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment);

        var successResultResponseSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());
        var failureResultPymtCnfAdvResponse =
            Result.Failure<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(StorageOperationErrorType
                .NotFound);

        _mockPaymentStorageService.Setup(x => x.SearchByTags(It.IsAny<Dictionary<string, string>>(), default))
            .ReturnsAsync(successResult);

        _mockSacAzStorageClient
            .Setup(x => x.PaymentService.GetAcceptPayment(It.IsAny<string>(), It.IsAny<Guid>(), default))
            .ReturnsAsync(expectedPaymentStatus);

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Get(It.Is<string>(t => t == payloadModel.Transaction.PaymentRequestLifecycleId),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResultPymtCnfAdvResponse);

        _mockMerchantService.Setup(service => service.GetMerchantFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(merchant);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.IsAny<NewPaymentConfirmationAdvice>(), It.IsAny<RequestHeader>()))
            .ThrowsAsync(new Exception());

        // _mockPaymentCnfResponseStorageService.Setup(_ =>
        //         _.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
        //     .ReturnsAsync(successResultResponseSaving);

        //Act
        var exception = Assert.ThrowsAsync<MockException>(async () =>
            await paymentService.GetPaymentStatusWebhook(input));
        // var result = await paymentService.GetPaymentStatusWebhook(input);

        //Assert
        Assert.NotNull(exception);
    }

    [Test]
    public async Task GetPaymentStatusWebhook_Returns_PaymentStatus_FromDatabase()
    {
        //Arrange
        var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var expectedPaymentStatus = new AcceptPaymentOutput
        {
            PaymentId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Status = new PaymentStatus
            {
                Code = PaymentStatusCode.Initiated,
                Events = new List<PaymentStatusChangeEvent>
                {
                    new()
                    {
                        Event = "Authorized",
                        Timestamp = DateTimeOffset.Now.DateTime
                    },
                    new()
                    {
                        Event = "Initiated",
                        Timestamp = DateTimeOffset.Now.DateTime
                    }
                }
            },
            DestinationId = Guid.Parse("472e651e-5a1e-424d-8098-23858bf03ad7"),
            Currency = Currency.GBP.ToString(),
            Amount = 100
        };

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645"
        };

        var expectedZappResponse = new PaymentConfirmationAdvicesResponse
        {
            initiatingPartyId = "123",
            messageId = "123",
            creationDateTime = DateTime.Now,
            originalMessageId = "123",
            paymentRequestLifecycleId = "123",
            requestStatus = new RequestStatus { paymentRequestStatus = "APPR" },
            agreementStatus = new AgreementStatus { agreementId = "123", agreementStatus = "APRD" }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated()
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var successResult = Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment);

        var successResultPymtCnfAdvSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());

        _mockPaymentStorageService.Setup(x => x.SearchByTags(It.IsAny<Dictionary<string, string>>(), default))
            .ReturnsAsync(successResult);

        _mockSacAzStorageClient
            .Setup(x => x.PaymentService.GetAcceptPayment(It.IsAny<string>(), It.IsAny<Guid>(), default))
            .ReturnsAsync(expectedPaymentStatus);

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Get(It.Is<string>(t => t == payloadModel.Transaction.PaymentRequestLifecycleId),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultPymtCnfAdvSaving);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.IsAny<NewPaymentConfirmationAdvice>(), It.IsAny<RequestHeader>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedZappResponse, 200)));

        _mockPaymentCnfResponseStorageService.Setup(_ =>
                _.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultPymtCnfAdvSaving);

        _mockPaymentStorageService.Setup(service => service.Update(It.IsAny<PaymentStorageEntity>(),
                default, StorageLockType.LastWriterWins))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        //Act
        var result = await paymentService.GetPaymentStatusWebhook(input);

        //Assert
        Assert.NotNull(result);
    }

    [Test]
    public async Task GetPaymentStatusWebhook_Returns_PaymentStatus_ForEmptyPaymentId()
    {
        //Arrange
        var paymentId = Guid.Empty.ToString(); //  var paymentId = "472e651e-5a1e-424d-8098-23858bf03ad7";

        var payment = new PaymentStorageEntity
        {
            SacAzPaymentId = "3f2668bc-39ac-402c-913a-ed0658a2752c",
            ZappDistributorId = "000645",
            PaymentConfirmAdviseSent = false
        };

        var expectedZappResponse = new PaymentConfirmationAdvicesResponse
        {
            initiatingPartyId = "123",
            messageId = "123",
            creationDateTime = DateTime.Now,
            originalMessageId = "123",
            paymentRequestLifecycleId = "123",
            requestStatus = new RequestStatus { paymentRequestStatus = "APPR" },
            agreementStatus = new AgreementStatus { agreementId = "123", agreementStatus = "APRD" }
        };

        var header = new RequestHeader
        {
            HeaderRequestId = "7eab4eab35a542e085add0363a49c035",
            HeaderProductId = "PBARFP",
            HeaderParticipantId = "000545",
            HeaderJwsSignature = "X-JWS-Signature",
            HeaderIdempotencyKey = "Idempotency-Key"
        };

        var input = new WebhookRequest
        {
            Type = "AcceptPaymentStatusUpdated",
            Data = new AcceptPaymentStatusUpdated
            {
                PaymentId = Guid.Parse(paymentId),
                ExecutionTime = DateTime.UtcNow
            },
            RetryCount = 0,
            PaymentRequestStatusRetrievalLifecycleId = "111123123123123111"
        };

        var payloadModel = new PaymentPayload
        {
            InitiatingPartyId = "000545",
            MessageId = "7eab4eab35a542e085add0363a49c035",
            CreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
            BusinessType = 3,
            Debtor = new DebtorForPWA
            {
                DebtorId = "Fxrsi5345",
                DebtorServiceProviderId = "000369"
            },
            Creditor = new CreditorForPWA
            {
                CreditorId = "VerizonoMobile000588",
                CreditorServiceProviderId = "000645",
                CreditorCategoryCode = "0742",
                CreditorTradeName = "VerizonoMobileLtd",
                CreditorReturnString = "https://www.creditor.com/creditorreturnstring",
                CreditorLogoUrl = "https://www.cdn.com/creditorlogourl",
                CreditorAccount = new CreditorAccount
                {
                    AccountNumber = "45122351223323",
                    ClearingSystem = ClearingSystem.FPS.ToString(),
                    AccountType = "PERS",
                    AccountName = "Verizono"
                },
                CreditorAssignedDebtorId = "7eab4eab35a542e085add0363a49c156"
            },
            Transaction = new PaymentPayloadTransaction
            {
                AgreementId = "a3e2a749088440eab8b40c926efe2931",
                AgreementType = AgreementType.AOF,
                PaymentRequestLifecycleId = "923123123123123100",
                EndToEndId = "7jhg5eab35a542e085add0363a4423o6",
                InstructionId = "98dj4eab35a542e085add0363a40jk564",
                ConfirmationExpiryTimeInterval = 150,
                PaymentRequestType = PaymentRequestType.IMDT,
                TransactionCreationDateTime = new DateTime(2023, 10, 20, 06, 57, 24, DateTimeKind.Utc),
                InstructedAmount = new Amount
                {
                    Currency = Currency.GBP.ToString(),
                    Value = 100.25
                },
                Purpose = "ONLN",
                CategoryPurpose = CategoryPurpose.PYMT.ToString(),
                Restriction = "18PLUS",
                StrongCustomerAuthentication = true,
                DebtorInteractionType = "INSN"
            },
            Headers = header
        };

        var successResultPymtCnfAdvSaving =
            Result.Success<PaymentCnfAdviseResponseStorageEntity, StorageOperationErrorType>(
                new PaymentCnfAdviseResponseStorageEntity());

        _mockPaymentPayloadService.Setup(x => x.GetPaymentPayloadFromDb(It.IsAny<string>(), default))
            .ReturnsAsync(payloadModel);

        _mockPaymentCnfResponseStorageService.Setup(m =>
                m.Get(It.Is<string>(t => t == payloadModel.Transaction.PaymentRequestLifecycleId),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultPymtCnfAdvSaving);

        _mockZappClient.Setup(client =>
                client.CallZappEndpoint(It.IsAny<string>(),
                    It.IsAny<NewPaymentConfirmationAdvice>(), It.IsAny<RequestHeader>()))
            .ReturnsAsync((Func<(object Result, int statusCode)>)(() => (expectedZappResponse, 200)));

        _mockPaymentCnfResponseStorageService.Setup(_ =>
                _.Create(It.IsAny<PaymentCnfAdviseResponseStorageEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResultPymtCnfAdvSaving);

        _mockPaymentStorageService.Setup(service => service.Update(It.IsAny<PaymentStorageEntity>(),
                default, StorageLockType.LastWriterWins))
            .ReturnsAsync(Result.Success<PaymentStorageEntity, StorageOperationErrorType>(payment));

        //Act
        var result = await paymentService.GetPaymentStatusWebhook(input, payment);

        //Assert
        Assert.NotNull(result);
    }

    [Test]
    public async Task GetPaymentFromCache_Returns_Payment()
    {
        //Arrange
        var lifecycleId = "923123123123123100";

        var payment = new PaymentStorageEntity
        {
            ZappPaymentId = lifecycleId,
            DestinationId = "f446f245-4e30-4e15-a349-77e48ed4c9b9",
            ZappDistributorId = "000645",
            SacAzPaymentId = "",
            SacAzPaymentUrl = "",
            Created = DateTimeOffset.Now.AddMinutes(-100)
        };

        _mockConcurrentMemoryCache.Setup(m =>
            m.Get<PaymentStorageEntity>(It.IsAny<string>())
        ).Returns(payment);

        //Act
        var result = await paymentService.GetPaymentFromCache(lifecycleId);

        //Assert
        Assert.NotNull(result);
        result.Should().BeEquivalentTo(payment);
    }

    [Test]
    public async Task GetPaymentFromCache_Returns_Payment_WithGettingFromStorage()
    {
        //Arrange
        var lifecycleId = "923123123123123100";

        var payment = new PaymentStorageEntity
        {
            ZappPaymentId = lifecycleId,
            DestinationId = "f446f245-4e30-4e15-a349-77e48ed4c9b9",
            ZappDistributorId = "000645",
            SacAzPaymentId = "",
            SacAzPaymentUrl = "",
            Created = DateTimeOffset.Now.AddMinutes(-100)
        };

        _mockConcurrentMemoryCache.Setup(m =>
            m.Get<PaymentStorageEntity>(It.IsAny<string>())
        ).Returns((PaymentStorageEntity)null);

        _mockConcurrentMemoryCache.Setup(m =>
            m.GetOrCreateAsync(It.IsAny<string>(),
                It.IsAny<Func<ICacheEntry, Task<PaymentStorageEntity>>>())
        ).ReturnsAsync(payment);

        //Act
        var result = await paymentService.GetPaymentFromCache(lifecycleId);

        //Assert
        Assert.NotNull(result);
        result.Should().BeEquivalentTo(payment);
    }

    [Test]
    public async Task GetPaymentFromCache_Throws_Error()
    {
        //Arrange
        var lifecycleId = "923123123123123100";

        var failureResult =
            Result.Failure<PaymentStorageEntity, StorageOperationErrorType>(StorageOperationErrorType.NotFound);

        _mockConcurrentMemoryCache.Setup(m =>
            m.Get<PaymentStorageEntity>(It.IsAny<string>())
        ).Returns((PaymentStorageEntity)null);

        _mockConcurrentMemoryCache.Setup(m =>
            m.GetOrCreateAsync(It.IsAny<string>(),
                It.IsAny<Func<ICacheEntry, Task<PaymentStorageEntity>>>())
        ).ThrowsAsync(AdapterException.BadInputCode("Payment with zappPaymentId is not found."));

        _mockPaymentStorageService.Setup(m => m.Get(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResult);

        //Act
        var exception = Assert.ThrowsAsync<AdapterException>(async () =>
            await paymentService.GetPaymentFromCache(lifecycleId));

        //Assert
        exception.ErrorMessage.Should().BeEquivalentTo("Payment with zappPaymentId is not found.");
        exception.ApiErrorCode.Should().Be(AdapterErrorCode.InvalidInput);
        exception.ApiHttpStatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}