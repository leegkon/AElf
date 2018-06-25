using System.Threading.Tasks;

namespace AElf.Kernel.KernelAccount
{
    public interface ISmartContractZero : ISmartContract
    {
        Task<Hash> DeploySmartContract(int category, byte[] contrac);
    }
}