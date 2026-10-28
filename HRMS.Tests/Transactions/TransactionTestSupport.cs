using System.Data.Common;
using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Audit;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Transactions;

/// <summary>Counts the REAL database transaction events raised by EF Core / Npgsql (not a mock of a transaction interface).</summary>
public sealed class TransactionCounter : DbTransactionInterceptor
{
    private int _started;
    private int _committed;
    private int _rolledBack;

    public int Started => Volatile.Read(ref _started);

    public int Committed => Volatile.Read(ref _committed);

    public int RolledBack => Volatile.Read(ref _rolledBack);

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Interlocked.Increment(ref _started);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _started);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => Interlocked.Increment(ref _committed);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _committed);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => Interlocked.Increment(ref _rolledBack);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _rolledBack);
        return Task.CompletedTask;
    }
}

public sealed record TxAccount(Guid CompanyId, Guid UserId, string Domain);

public sealed class FakeCurrentUser : ICurrentUser
{
    private readonly Guid _userId;
    private readonly Guid _companyId;

    public FakeCurrentUser(Guid userId, Guid companyId)
    {
        _userId = userId;
        _companyId = companyId;
    }

    public bool IsResolved => true;

    public Guid UserId => _userId;

    public Guid CompanyId => _companyId;

    public string RoleKey => "hr_admin";

    public string RoleDisplayName => "HR Administrator";

    public Guid? EmployeeId => null;

    public bool PasswordChangeRequired => false;

    public bool IsPlatform => false;

    public IReadOnlyDictionary<string, string[]> Permissions => RolePermissions.Empty;

    public bool HasPermission(string module, string action) => false;
}

public sealed class FakeIp : IClientInfo
{
    public string? IpAddress => "198.51.100.9";

    public string? UserAgent => null;
}

/// <summary>Observations made by test handlers while they run (what another connection can see mid-operation, etc.).</summary>
public sealed class TxProbe
{
    public string? NameSeenByOtherConnectionAfterFirstSave { get; set; }

    public int CommitsAfterFirstSave { get; set; } = -1;

    public bool? HadTransactionInHandler { get; set; }

    public Action? Cancel { get; set; }
}

public enum TxMode
{
    Succeed,
    ThrowAfterFirstSave,
    ThrowAfterAuditStaged,
    ExecuteUpdateThenThrow,
    CancelAfterFirstSave,
    ForgetFinalSave,
    NoAuditStaged,
    StageWrongAction,
    StageWrongModule,
    StageExpectedAndExtra,
    CancelThenReturnWithoutSaving
}

[AuditExpectation("company", "company.updated")]
public sealed record TxCommand(Guid CompanyId, string NewName, TxMode Mode) : IRequest<string>, IAuditedRequest;

/// <summary>Declares two entries; the handler stages the listed actions (all in module "company").</summary>
[AuditExpectation("company", "company.updated")]
[AuditExpectation("company", "company.renamed")]
public sealed record MultiAuditCommand(Guid CompanyId, string[] StageActions) : IRequest<string>, IAuditedRequest;

/// <summary>Declares the SAME entry twice: one staged entry must not satisfy both declarations.</summary>
[AuditExpectation("company", "company.updated")]
[AuditExpectation("company", "company.updated")]
public sealed record DuplicateExpectationCommand(Guid CompanyId, int StageCount) : IRequest<string>, IAuditedRequest;

/// <summary>Audited but declares nothing: the pipeline must refuse to run it.</summary>
public sealed record UndeclaredAuditedCommand(Guid CompanyId) : IRequest<string>, IAuditedRequest;

/// <summary>Stages its own matching entry, then sends an inner command expecting the same entry but staging none.</summary>
[AuditExpectation("company", "company.updated")]
public sealed record WindowOuterCommand(Guid CompanyId) : IRequest<string>, IAuditedRequest;

[AuditExpectation("company", "company.updated")]
public sealed record WindowInnerCommand(Guid CompanyId) : IRequest<string>, IAuditedRequest;

/// <summary>Transactional but not audited: must commit without an audit entry.</summary>
public sealed record TxPlainCommand(Guid CompanyId, string NewName) : IRequest<string>, ITransactionalRequest;

/// <summary>A query: not marked, must not be wrapped.</summary>
public sealed record ReadCompanyNameQuery(Guid CompanyId) : IRequest<string>;

[AuditExpectation("company", "company.updated")]
public sealed record OuterCommand(Guid CompanyId, string OuterName, string InnerName, bool InnerFails) : IRequest<string>, IAuditedRequest;

[AuditExpectation("company", "company.renamed")]
public sealed record InnerCommand(Guid CompanyId, string Name, bool Fails) : IRequest<string>, IAuditedRequest;

public sealed record InvalidTxCommand(string Name) : IRequest<string>, ITransactionalRequest;

public sealed class InvalidTxCommandValidator : AbstractValidator<InvalidTxCommand>
{
    public InvalidTxCommandValidator() => RuleFor(c => c.Name).NotEmpty();
}

public sealed class TxHandler : IRequestHandler<TxCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly TxProbe _probe;
    private readonly TransactionCounter _counter;
    private readonly DbContextOptions<HrmsDbContext> _options;

    public TxHandler(HrmsDbContext db, IAuditWriter audit, TxProbe probe, TransactionCounter counter, DbContextOptions<HrmsDbContext> options)
    {
        _db = db;
        _audit = audit;
        _probe = probe;
        _counter = counter;
        _options = options;
    }

    public async Task<string> Handle(TxCommand command, CancellationToken cancellationToken)
    {
        _probe.HadTransactionInHandler = _db.Database.CurrentTransaction is not null;

        var company = await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken);
        var oldName = company.Name;
        company.Name = command.NewName;

        // What a repository does: it saves by itself. Inside the unit of work this must NOT make anything visible yet.
        await _db.SaveChangesAsync(cancellationToken);

        _probe.NameSeenByOtherConnectionAfterFirstSave = await TxHelpers.NameViaOtherConnectionAsync(_options, command.CompanyId);
        _probe.CommitsAfterFirstSave = _counter.Committed;

        switch (command.Mode)
        {
            case TxMode.ThrowAfterFirstSave:
                throw new InvalidOperationException("simulated failure after the first successful save");

            case TxMode.CancelAfterFirstSave:
                _probe.Cancel?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                break;

            case TxMode.ExecuteUpdateThenThrow:
                // A bulk update is executed immediately, outside SaveChanges; it must still be part of the transaction.
                await _db.Companies.Where(c => c.Id == command.CompanyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.LogoUrl, "bulk-changed"), cancellationToken);
                throw new InvalidOperationException("simulated failure after a bulk update");
        }

        switch (command.Mode)
        {
            case TxMode.NoAuditStaged:
                break;

            case TxMode.StageWrongAction:
                _audit.Stage(new AuditEntry("company", "company.deleted", command.CompanyId));
                break;

            case TxMode.StageWrongModule:
                _audit.Stage(new AuditEntry("employee", "company.updated", command.CompanyId));
                break;

            case TxMode.StageExpectedAndExtra:
                _audit.Stage(new AuditEntry("company", "company.viewed", command.CompanyId));
                _audit.Stage(new AuditEntry("company", "company.updated", command.CompanyId, new { Name = oldName }, new { Name = command.NewName }));
                break;

            default:
                _audit.Stage(new AuditEntry("company", "company.updated", command.CompanyId, new { Name = oldName }, new { Name = command.NewName }));
                break;
        }

        if (command.Mode == TxMode.ThrowAfterAuditStaged)
        {
            throw new InvalidOperationException("simulated failure after the audit entry was staged");
        }

        if (command.Mode == TxMode.CancelThenReturnWithoutSaving)
        {
            // The handler "succeeds" but the request was cancelled before the unit of work's final flush.
            _probe.Cancel?.Invoke();
            return oldName;
        }

        if (command.Mode != TxMode.ForgetFinalSave)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return oldName;
    }
}

public sealed class TxPlainHandler : IRequestHandler<TxPlainCommand, string>
{
    private readonly HrmsDbContext _db;

    public TxPlainHandler(HrmsDbContext db) => _db = db;

    public async Task<string> Handle(TxPlainCommand command, CancellationToken cancellationToken)
    {
        var company = await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken);
        company.Name = command.NewName;
        await _db.SaveChangesAsync(cancellationToken);
        return company.Name;
    }
}

public sealed class ReadCompanyNameHandler : IRequestHandler<ReadCompanyNameQuery, string>
{
    private readonly HrmsDbContext _db;
    private readonly TxProbe _probe;

    public ReadCompanyNameHandler(HrmsDbContext db, TxProbe probe)
    {
        _db = db;
        _probe = probe;
    }

    public async Task<string> Handle(ReadCompanyNameQuery query, CancellationToken cancellationToken)
    {
        _probe.HadTransactionInHandler = _db.Database.CurrentTransaction is not null;

        return await _db.Companies.AsNoTracking().Where(c => c.Id == query.CompanyId).Select(c => c.Name).SingleAsync(cancellationToken);
    }
}

public sealed class OuterHandler : IRequestHandler<OuterCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IMediator _mediator;

    public OuterHandler(HrmsDbContext db, IAuditWriter audit, IMediator mediator)
    {
        _db = db;
        _audit = audit;
        _mediator = mediator;
    }

    public async Task<string> Handle(OuterCommand command, CancellationToken cancellationToken)
    {
        var company = await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken);
        company.Name = command.OuterName;
        _audit.Stage(new AuditEntry("company", "company.updated", command.CompanyId, newValue: new { Name = command.OuterName }));
        await _db.SaveChangesAsync(cancellationToken);

        return await _mediator.Send(new InnerCommand(command.CompanyId, command.InnerName, command.InnerFails), cancellationToken);
    }
}

public sealed class InnerHandler : IRequestHandler<InnerCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;

    public InnerHandler(HrmsDbContext db, IAuditWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<string> Handle(InnerCommand command, CancellationToken cancellationToken)
    {
        var company = await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken);
        company.Name = command.Name;
        _audit.Stage(new AuditEntry("company", "company.renamed", command.CompanyId, newValue: new { Name = command.Name }));
        await _db.SaveChangesAsync(cancellationToken);

        if (command.Fails)
        {
            throw new InvalidOperationException("simulated inner failure");
        }

        return command.Name;
    }
}

public sealed class InvalidTxHandler : IRequestHandler<InvalidTxCommand, string>
{
    public Task<string> Handle(InvalidTxCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The handler must never run for an invalid request.");
}

public sealed class MultiAuditHandler : IRequestHandler<MultiAuditCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;

    public MultiAuditHandler(HrmsDbContext db, IAuditWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<string> Handle(MultiAuditCommand command, CancellationToken cancellationToken)
    {
        (await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken)).Name = "Multi audit change";

        foreach (var action in command.StageActions)
        {
            _audit.Stage(new AuditEntry("company", action, command.CompanyId));
        }

        await _db.SaveChangesAsync(cancellationToken);
        return "done";
    }
}

public sealed class DuplicateExpectationHandler : IRequestHandler<DuplicateExpectationCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;

    public DuplicateExpectationHandler(HrmsDbContext db, IAuditWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<string> Handle(DuplicateExpectationCommand command, CancellationToken cancellationToken)
    {
        (await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken)).Name = "Duplicate expectation change";

        for (var i = 0; i < command.StageCount; i++)
        {
            _audit.Stage(new AuditEntry("company", "company.updated", command.CompanyId));
        }

        await _db.SaveChangesAsync(cancellationToken);
        return "done";
    }
}

public sealed class UndeclaredAuditedHandler : IRequestHandler<UndeclaredAuditedCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;

    public UndeclaredAuditedHandler(HrmsDbContext db, IAuditWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<string> Handle(UndeclaredAuditedCommand command, CancellationToken cancellationToken)
    {
        (await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken)).Name = "Undeclared audited change";
        _audit.Stage(new AuditEntry("company", "company.updated", command.CompanyId));
        await _db.SaveChangesAsync(cancellationToken);
        return "must not run";
    }
}

public sealed class WindowOuterHandler : IRequestHandler<WindowOuterCommand, string>
{
    private readonly HrmsDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IMediator _mediator;

    public WindowOuterHandler(HrmsDbContext db, IAuditWriter audit, IMediator mediator)
    {
        _db = db;
        _audit = audit;
        _mediator = mediator;
    }

    public async Task<string> Handle(WindowOuterCommand command, CancellationToken cancellationToken)
    {
        (await _db.Companies.SingleAsync(c => c.Id == command.CompanyId, cancellationToken)).Name = "Window outer change";
        _audit.Stage(new AuditEntry("company", "company.updated", command.CompanyId));        // outer's own matching entry
        await _db.SaveChangesAsync(cancellationToken);

        return await _mediator.Send(new WindowInnerCommand(command.CompanyId), cancellationToken);
    }
}

public sealed class WindowInnerHandler : IRequestHandler<WindowInnerCommand, string>
{
    // Deliberately stages nothing: the outer command's earlier "company.updated" entry must not satisfy this command.
    public Task<string> Handle(WindowInnerCommand command, CancellationToken cancellationToken) => Task.FromResult("inner done");
}

/// <summary>Runs test code at the real transaction events (for example cancelling the request just before the commit).</summary>
public sealed class TransactionHookInterceptor : DbTransactionInterceptor
{
    public Action? OnCommitting { get; set; }

    public Action? OnRollingBack { get; set; }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        OnCommitting?.Invoke();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        OnCommitting?.Invoke();
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        OnRollingBack?.Invoke();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        OnRollingBack?.Invoke();
        return result;
    }
}

public static class TxHelpers
{
    /// <summary>Reads through a brand-new connection: sees committed data only.</summary>
    public static async Task<string> NameViaOtherConnectionAsync(DbContextOptions<HrmsDbContext> options, Guid companyId)
    {
        await using var other = new HrmsDbContext(options);
        return await other.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).SingleAsync();
    }

    public static async Task<TxAccount> CreateAccountAsync(PostgresFixture fx)
    {
        await fx.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var company = new Company { Id = Guid.NewGuid(), Name = "Original " + suffix, Domain = $"tx-{suffix}.test", LogoUrl = "original-logo", Timezone = "Asia/Kolkata", IsActive = true };
        var user = new User
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, RoleId = SystemRoles.HrAdminRoleId, Name = "Tx User",
            Email = $"tx-{suffix}@{company.Domain}", PasswordHash = "not-a-real-hash", IsActive = true
        };

        await using var db = fx.CreateDbContext();
        db.Companies.Add(company);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new TxAccount(company.Id, user.Id, company.Domain);
    }

    public static async Task<(string Name, string? Logo)> CompanyAsync(PostgresFixture fx, Guid companyId)
    {
        await using var db = fx.CreateDbContext();
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == companyId);
        return (company.Name, company.LogoUrl);
    }

    public static async Task<List<AuditLog>> AuditRowsAsync(PostgresFixture fx, Guid companyId)
    {
        await using var db = fx.CreateDbContext();
        return await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(a => a.CreatedAt).ToListAsync();
    }
}

/// <summary>A small DI container with the real unit of work, audit writer and transaction behavior over the guarded PostgreSQL.</summary>
public sealed class TxHost : IDisposable
{
    public TxHost(PostgresFixture fx, TxAccount account)
    {
        fx.VerifyTarget();

        Account = account;
        Counter = new TransactionCounter();
        Probe = new TxProbe();
        Hooks = new TransactionHookInterceptor();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Counter);
        services.AddSingleton(Probe);
        services.AddDbContext<HrmsDbContext>(o => o.UseNpgsql(fx.ConnectionString).AddInterceptors(Counter, Hooks));
        services.AddScoped<ITenantContext>(_ =>
        {
            var tenant = new TenantContext();
            tenant.SetTenant(account.CompanyId, account.Domain);
            return tenant;
        });
        services.AddScoped<ICurrentUser>(_ => new FakeCurrentUser(account.UserId, account.CompanyId));
        services.AddScoped<IClientInfo, FakeIp>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditWriter>());
        services.AddScoped<IAuditStagingState>(sp => sp.GetRequiredService<AuditWriter>());
        services.AddScoped<PostCommitActions>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddValidatorsFromAssemblyContaining<InvalidTxCommandValidator>();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<TxCommand>();
            cfg.AddOpenBehavior(typeof(HRMS.Application.Common.Behaviors.ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(HRMS.Application.Common.Behaviors.TransactionBehavior<,>));
        });

        Provider = services.BuildServiceProvider();
    }

    public TxAccount Account { get; }

    public TransactionCounter Counter { get; }

    public TxProbe Probe { get; }

    public TransactionHookInterceptor Hooks { get; }

    public ServiceProvider Provider { get; }

    public async Task<T> SendAsync<T>(IRequest<T> request, CancellationToken cancellationToken = default)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, cancellationToken);
    }

    public void Dispose() => Provider.Dispose();
}
