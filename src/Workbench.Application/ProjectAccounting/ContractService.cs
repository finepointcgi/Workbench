using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

public sealed class ContractService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : IContractService
{
    public async Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<Customer>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken)).Select(c => c.ToDto()).ToList();
    }

    public async Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var code = request.Code.Required("Code");
        if (await db.Set<Customer>().AnyAsync(c => c.Code == code, cancellationToken))
        {
            throw new ArgumentException($"Customer code '{code}' is already used.");
        }

        var customer = new Customer { Code = code, Name = request.Name.Required("Name"), Category = request.Category?.Trim() };
        db.Set<Customer>().Add(customer);
        audit.Record(db, nameof(Customer), customer.Id, "Created", after: customer.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return customer.ToDto();
    }

    public async Task<IReadOnlyList<ContractDto>> ListContractsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var contracts = await db.Set<Contract>().AsNoTracking().OrderBy(c => c.ContractNumber).ToListAsync(cancellationToken);
        return contracts.Select(c => c.ToDto(customers.GetValueOrDefault(c.CustomerId, ""))).ToList();
    }

    public async Task<ContractDto?> GetContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == contract.CustomerId, cancellationToken);
        return contract.ToDto(customer.Name);
    }

    public async Task<ContractDto> CreateContractAsync(SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = new Contract { ContractNumber = "", Name = "" };
        var customerName = await ApplyAsync(db, contract, request, cancellationToken);
        db.Set<Contract>().Add(contract);
        audit.Record(db, nameof(Contract), contract.Id, "Created", after: contract.ToDto(customerName));
        await db.SaveChangesAsync(cancellationToken);
        return contract.ToDto(customerName);
    }

    public async Task<ContractDto> UpdateContractAsync(Guid contractId, SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = await db.Set<Contract>().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        var oldCustomer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == contract.CustomerId, cancellationToken);
        var before = contract.ToDto(oldCustomer.Name);
        var customerName = await ApplyAsync(db, contract, request, cancellationToken);
        audit.Record(db, nameof(Contract), contract.Id, "Updated", before, contract.ToDto(customerName));
        await db.SaveChangesAsync(cancellationToken);
        return contract.ToDto(customerName);
    }

    public async Task<IReadOnlyList<BillRateDto>> ListBillRatesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rates = await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contractId).ToListAsync(cancellationToken);
        return rates.OrderBy(r => r.LaborCategoryId).ThenByDescending(r => r.EffectiveFrom).Select(r => r.ToDto()).ToList();
    }

    public async Task<BillRateDto> AddBillRateAsync(Guid contractId, AddBillRateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.HourlyBillRate <= 0)
        {
            throw new ArgumentException("Bill rate must be greater than zero.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        if (!await db.Set<LaborCategory>().AnyAsync(l => l.Id == request.LaborCategoryId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Labor category", request.LaborCategoryId);
        }

        if (await db.Set<ContractLaborRate>().AnyAsync(r => r.ContractId == contractId && r.LaborCategoryId == request.LaborCategoryId && r.EffectiveFrom == request.EffectiveFrom, cancellationToken))
        {
            throw new ArgumentException("A bill rate for that labor category and date already exists.");
        }

        var rate = new ContractLaborRate
        {
            ContractId = contractId,
            LaborCategoryId = request.LaborCategoryId,
            EffectiveFrom = request.EffectiveFrom,
            HourlyBillRate = request.HourlyBillRate,
        };
        db.Set<ContractLaborRate>().Add(rate);
        audit.Record(db, nameof(ContractLaborRate), rate.Id, "Created", after: rate.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return rate.ToDto();
    }

    private static async Task<string> ApplyAsync(IWorkbenchDbContext db, Contract contract, SaveContractRequest request, CancellationToken cancellationToken)
    {
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CustomerId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Customer", request.CustomerId);
        var number = request.ContractNumber.Required("Contract number");
        if (await db.Set<Contract>().AnyAsync(c => c.Id != contract.Id && c.ContractNumber == number, cancellationToken))
        {
            throw new ArgumentException($"Contract number '{number}' is already used.");
        }

        if (request.PeriodEnd < request.PeriodStart)
        {
            throw new ArgumentException("Period of performance end can't be before its start.");
        }

        if (request.CeilingValue < 0 || request.FundedValue < 0)
        {
            throw new ArgumentException("Ceiling and funded values can't be negative.");
        }

        if (request.FundedValue > request.CeilingValue)
        {
            throw new ArgumentException("Funded value can't exceed the ceiling.");
        }

        contract.CustomerId = customer.Id;
        contract.ContractNumber = number;
        contract.Name = request.Name.Required("Name");
        contract.Type = (ContractType)(int)request.Type;
        contract.Status = (ContractStatus)(int)request.Status;
        contract.IsPrime = request.IsPrime;
        contract.PeriodStart = request.PeriodStart;
        contract.PeriodEnd = request.PeriodEnd;
        contract.CeilingValue = request.CeilingValue;
        contract.FundedValue = request.FundedValue;
        return customer.Name;
    }
}
