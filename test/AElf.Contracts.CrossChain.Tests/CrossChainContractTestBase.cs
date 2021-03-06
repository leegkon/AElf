using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acs3;
using Acs7;
using AElf.Contracts.Consensus.AEDPoS;
using AElf.Contracts.MultiToken;
using AElf.Contracts.ParliamentAuth;
using AElf.Contracts.TestKet.AEDPoSExtension;
using AElf.Contracts.TestKit;
using AElf.CrossChain;
using AElf.Cryptography.ECDSA;
using AElf.Kernel;
using AElf.Kernel.Consensus;
using AElf.Kernel.Token;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Volo.Abp.Threading;

namespace AElf.Contracts.CrossChain.Tests
{
    public class CrossChainContractTestBase : AEDPoSExtensionTestBase
    {
        #region Contract Address

        public Address TokenContractAddress =>
            ContractAddresses[TokenSmartContractAddressNameProvider.Name];

        protected Address ParliamentAuthContractAddress =>
            ContractAddresses[ParliamentAuthSmartContractAddressNameProvider.Name];

        public Address CrossChainContractAddress =>
            ContractAddresses[CrossChainSmartContractAddressNameProvider.Name];

        public Address ConsensusContractAddress =>
            ContractAddresses[ConsensusSmartContractAddressNameProvider.Name];

        #endregion

        protected ECKeyPair DefaultKeyPair => SampleECKeyPairs.KeyPairs[0];

        protected static List<ECKeyPair> InitialCoreDataCenterKeyPairs =>
            SampleECKeyPairs.KeyPairs.Take(AEDPoSExtensionConstants.InitialKeyPairCount).ToList();

        protected Address DefaultSender => Address.FromPublicKey(DefaultKeyPair.PublicKey);

        internal AEDPoSContractImplContainer.AEDPoSContractImplStub ConsensusStub =>
            GetTester<AEDPoSContractImplContainer.AEDPoSContractImplStub>(
                ContractAddresses[ConsensusSmartContractAddressNameProvider.Name],
                DefaultKeyPair);

        #region Token

        internal TokenContractContainer.TokenContractStub TokenContractStub =>
            GetTester<TokenContractContainer.TokenContractStub>(
                ContractAddresses[TokenSmartContractAddressNameProvider.Name],
                SampleECKeyPairs.KeyPairs[0]);

        #endregion

        #region Paliament

        internal ParliamentAuthContractContainer.ParliamentAuthContractStub ParliamentAuthContractStub =>
            GetParliamentAuthContractTester(DefaultKeyPair);

        internal ParliamentAuthContractContainer.ParliamentAuthContractStub GetParliamentAuthContractTester(
            ECKeyPair keyPair)
        {
            return GetTester<ParliamentAuthContractContainer.ParliamentAuthContractStub>(ParliamentAuthContractAddress,
                keyPair);
        }

        #endregion

        internal CrossChainContractContainer.CrossChainContractStub CrossChainContractStub =>
            GetCrossChainContractStub(DefaultKeyPair);

        internal CrossChainContractContainer.CrossChainContractStub GetCrossChainContractStub(
            ECKeyPair keyPair)
        {
            return GetTester<CrossChainContractContainer.CrossChainContractStub>(
                CrossChainContractAddress,
                keyPair);
        }

        public CrossChainContractTestBase()
        {
            ContractAddresses = AsyncHelper.RunSync(() => DeploySystemSmartContracts(new List<Hash>
            {
                TokenSmartContractAddressNameProvider.Name,
                ParliamentAuthSmartContractAddressNameProvider.Name,
                CrossChainSmartContractAddressNameProvider.Name,
                ConsensusSmartContractAddressNameProvider.Name
            }));

            AsyncHelper.RunSync(InitializeTokenAsync);
            AsyncHelper.RunSync(InitializeParliamentContractAsync);
        }

        protected async Task InitializeCrossChainContractAsync(long parentChainHeightOfCreation = 0,
            int parentChainId = 0, bool withException = false)
        {
            await BlockMiningService.MineBlockAsync(new List<Transaction>
            {
                CrossChainContractStub.Initialize.GetTransaction(new InitializeInput
                {
                    ParentChainId = parentChainId == 0 ? ChainHelper.ConvertBase58ToChainId("AELF") : parentChainId,
                    CreationHeightOnParentChain = parentChainHeightOfCreation
                })
            }, withException);
        }

        internal async Task<int> InitAndCreateSideChainAsync(long parentChainHeightOfCreation = 0,
            int parentChainId = 0, long lockedTokenAmount = 10, bool withException = false)
        {
            await InitializeCrossChainContractAsync(parentChainHeightOfCreation, parentChainId, withException);
            await ApproveBalanceAsync(lockedTokenAmount);
            var proposalId = await CreateSideChainProposalAsync(1, lockedTokenAmount);
            await ApproveWithMinersAsync(proposalId);

            var releaseTx =
                await CrossChainContractStub.ReleaseSideChainCreation.SendAsync(new ReleaseSideChainCreationInput
                    {ProposalId = proposalId});
            var sideChainCreatedEvent = SideChainCreatedEvent.Parser
                .ParseFrom(releaseTx.TransactionResult.Logs.First(l => l.Name.Contains(nameof(SideChainCreatedEvent)))
                    .NonIndexed);
            var chainId = sideChainCreatedEvent.ChainId;

            return chainId;
        }

        private async Task InitializeParliamentContractAsync()
        {
            var initializeResult = await ParliamentAuthContractStub.Initialize.SendAsync(
                new ParliamentAuth.InitializeInput
                {
                    GenesisOwnerReleaseThreshold = 1,
                    PrivilegedProposer = DefaultSender,
                    ProposerAuthorityRequired = false
                });
            CheckResult(initializeResult.TransactionResult);
        }

        private async Task InitializeTokenAsync()
        {
            const string symbol = "ELF";
            const long totalSupply = 100_000_000;
            await BlockMiningService.MineBlockAsync(new List<Transaction>
            {
                TokenContractStub.Create.GetTransaction(new CreateInput
                {
                    Symbol = symbol,
                    Decimals = 2,
                    IsBurnable = true,
                    TokenName = "elf token",
                    TotalSupply = totalSupply,
                    Issuer = DefaultSender,
                }),
                TokenContractStub.Issue.GetTransaction(new IssueInput
                {
                    Symbol = symbol,
                    Amount = totalSupply - 20 * 100_000L,
                    To = DefaultSender,
                    Memo = "Issue token to default user.",
                })
            });
        }

        protected async Task ApproveBalanceAsync(long amount)
        {
            await BlockMiningService.MineBlockAsync(new List<Transaction>
            {
                TokenContractStub.Approve.GetTransaction(new MultiToken.ApproveInput
                {
                    Spender = CrossChainContractAddress,
                    Symbol = "ELF",
                    Amount = amount
                }),
                TokenContractStub.GetAllowance.GetTransaction(new GetAllowanceInput
                {
                    Symbol = "ELF",
                    Owner = DefaultSender,
                    Spender = CrossChainContractAddress
                })
            });
        }

        internal async Task<GetAllowanceOutput> ApproveAndTransferOrganizationBalanceAsync(Address organizationAddress,long amount)
        {
            var approveInput = new MultiToken.ApproveInput
            {
                Spender = CrossChainContractAddress,
                Symbol = "ELF",
                Amount = amount
            };
            var proposal = (await ParliamentAuthContractStub.CreateProposal.SendAsync(new CreateProposalInput
            {
                ContractMethodName = nameof(TokenContractStub.Approve),
                ExpiredTime = TimestampHelper.GetUtcNow().AddDays(1),
                Params = approveInput.ToByteString(),
                ToAddress = TokenContractAddress,
                OrganizationAddress = organizationAddress
            })).Output;
            await ApproveWithMinersAsync(proposal);
            await ReleaseProposalAsync(proposal);
            
            await TokenContractStub.Transfer.SendAsync(new TransferInput
            {
                Symbol = "ELF",
                Amount = amount,
                To = organizationAddress
            });
            
            var allowance = (await TokenContractStub.GetAllowance.CallAsync(new GetAllowanceInput
            {
                Symbol = "ELF",
                Owner = organizationAddress,
                Spender = CrossChainContractAddress
            }));
            
            return allowance;
        }

        internal async Task<Hash> CreateSideChainProposalAsync(long indexingPrice, long lockedTokenAmount, IEnumerable<ResourceTypeBalancePair> resourceTypeBalancePairs = null)
        {
            var createProposalInput = CreateSideChainCreationRequest(indexingPrice, lockedTokenAmount);
            var requestSideChainCreation =
                await CrossChainContractStub.RequestSideChainCreation.SendAsync(createProposalInput);
            
            var proposalId = ProposalCreated.Parser.ParseFrom(requestSideChainCreation.TransactionResult.Logs
                .First(l => l.Name.Contains(nameof(ProposalCreated))).NonIndexed).ProposalId;
            return proposalId;
        }

        internal async Task<Hash> CreateProposalAsync(string method,Address address,IMessage input)
        {
            var proposal = (await ParliamentAuthContractStub.CreateProposal.SendAsync(new CreateProposalInput
            {
                ToAddress = CrossChainContractAddress,
                ContractMethodName = method,
                ExpiredTime = TimestampHelper.GetUtcNow().AddDays(1),
                OrganizationAddress = address,
                Params = input.ToByteString()
            })).Output;
            return proposal;
        }
        
        protected async Task<TransactionResult> ReleaseProposalAsync(Hash proposalId)
        {
            var transaction = await ParliamentAuthContractStub.Release.SendAsync(proposalId);
            return transaction.TransactionResult;
        }
        
        protected async Task<TransactionResult> ReleaseProposalWithExceptionAsync(Hash proposalId)
        {
            var transaction = await ParliamentAuthContractStub.Release.SendWithExceptionAsync(proposalId);
            return transaction.TransactionResult;
        }

        internal SideChainCreationRequest CreateSideChainCreationRequest(long indexingPrice, long lockedTokenAmount,
            IEnumerable<ResourceTypeBalancePair> resourceTypeBalancePairs = null)
        {
            var res = new SideChainCreationRequest
            {
                IndexingPrice = indexingPrice,
                LockedTokenAmount = lockedTokenAmount,
                SideChainTokenDecimals = 2,
                IsSideChainTokenBurnable = true,
                SideChainTokenTotalSupply = 1_000_000_000,
                SideChainTokenSymbol = "TE",
                SideChainTokenName = "TEST",
            };
//            if (resourceTypeBalancePairs != null)
//                res.ResourceBalances.AddRange(resourceTypeBalancePairs.Select(x =>
//                    ResourceTypeBalancePair.Parser.ParseFrom(x.ToByteString())));
            return res;
        }

        protected async Task ApproveWithMinersAsync(Hash proposalId)
        {
            foreach (var bp in InitialCoreDataCenterKeyPairs)
            {
                var tester = GetParliamentAuthContractTester(bp);
                var approveResult = await tester.Approve.SendAsync(new Acs3.ApproveInput
                {
                    ProposalId = proposalId,
                });
                CheckResult(approveResult.TransactionResult);
            }
        }

        internal async Task<bool> DoIndexAsync(CrossChainBlockData crossChainBlockData)
        {
            var txRes = await CrossChainContractStub.ProposeCrossChainIndexing.SendAsync(crossChainBlockData);
            var proposalId = ProposalCreated.Parser
                .ParseFrom(txRes.TransactionResult.Logs.First(l => l.Name.Contains(nameof(ProposalCreated))).NonIndexed)
                .ProposalId;
            await ApproveWithMinersAsync(proposalId);
            
            await CrossChainContractStub.ReleaseCrossChainIndexing.SendAsync(proposalId);
            return true;
        }

        internal async Task<Hash> DisposalSideChainProposalAsync(SInt32Value chainId)
        {
            var disposalInput = chainId;
            var organizationAddress = await ParliamentAuthContractStub.GetDefaultOrganizationAddress.CallAsync(new Empty());
            var proposal = (await ParliamentAuthContractStub.CreateProposal.SendAsync(new CreateProposalInput
            {
                ContractMethodName = nameof(CrossChainContractStub.DisposeSideChain),
                ExpiredTime = TimestampHelper.GetUtcNow().AddDays(1),
                Params = disposalInput.ToByteString(),
                ToAddress = CrossChainContractAddress,
                OrganizationAddress = organizationAddress
            })).Output;
            return proposal;
        }

        private void CheckResult(TransactionResult result)
        {
            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new Exception(result.Error);
            }
        }
        
        internal ParentChainBlockData CreateParentChainBlockData(long height, int sideChainId, Hash txMerkleTreeRoot)
        {
            return new ParentChainBlockData
            {
                ChainId = sideChainId,
                Height = height,
                TransactionStatusMerkleTreeRoot = txMerkleTreeRoot
            };
        }
    }
}