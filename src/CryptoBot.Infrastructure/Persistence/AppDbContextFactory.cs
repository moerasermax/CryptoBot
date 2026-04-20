using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoBot.Infrastructure.Persistence;

/// <summary>
/// EF Core 工具（<c>dotnet ef migrations / database update</c>）在設計階段建立
/// <see cref="AppDbContext"/> 的工廠。
///
/// 為什麼需要：Infrastructure 是 class library，沒有 Host / DI，
/// 工具跑 migration 時沒辦法走 <c>AddInfrastructure</c>。此工廠提供一條
/// 不依賴 DI 的獨立路徑，連線字串走檔案本地預設。
///
/// 執行時（Application 正式啟動）不會用到此工廠 — DI 註冊才是主線。
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite("Data Source=cryptobot.db");
        return new AppDbContext(builder.Options);
    }
}
