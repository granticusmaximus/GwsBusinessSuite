using GwsBusinessSuite.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Data;

public sealed class AppDbContextFactory(IDbContextFactory<ApplicationDbContext> factory) : IAppDbContextFactory
{
    public async Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => await factory.CreateDbContextAsync(cancellationToken);
}
