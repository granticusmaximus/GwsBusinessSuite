using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelPageLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LockedAtUnix",
                table: "WikiPages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedBy",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedContentProtected",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentProtected",
                table: "WikiPageRevisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SentinelDeviceKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Storage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAtUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAtUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelDeviceKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelLockPins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PinHash = table.Column<string>(type: "TEXT", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureWindowStartUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    LockedUntilUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelLockPins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelPasskeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    AaGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAtUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelPasskeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelUnlockGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelUnlockGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelDeviceKeys_Username",
                table: "SentinelDeviceKeys",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelLockPins_Username",
                table: "SentinelLockPins",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPasskeys_CredentialId",
                table: "SentinelPasskeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPasskeys_Username",
                table: "SentinelPasskeys",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelUnlockGrants_Username_SessionId",
                table: "SentinelUnlockGrants",
                columns: new[] { "Username", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelDeviceKeys");

            migrationBuilder.DropTable(
                name: "SentinelLockPins");

            migrationBuilder.DropTable(
                name: "SentinelPasskeys");

            migrationBuilder.DropTable(
                name: "SentinelUnlockGrants");

            migrationBuilder.DropColumn(
                name: "LockedAtUnix",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "LockedContentProtected",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "ContentProtected",
                table: "WikiPageRevisions");
        }
    }
}
