using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotionOAuthConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMode",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "internal");

            migrationBuilder.AddColumn<string>(
                name: "OAuthBotId",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OAuthConnectedAt",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthRefreshToken",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceIconUrl",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthenticationMode",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "OAuthBotId",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "OAuthConnectedAt",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "OAuthRefreshToken",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "WorkspaceIconUrl",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "NotionConnectorSettings");
        }
    }
}
