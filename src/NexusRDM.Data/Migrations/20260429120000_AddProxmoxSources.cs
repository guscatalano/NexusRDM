using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusRDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxmoxSources",
                columns: table => new
                {
                    Id                     = table.Column<Guid>  (type: "TEXT", nullable: false),
                    Name                   = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BaseUrl                = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AuthMode               = table.Column<int>   (type: "INTEGER", nullable: false),
                    AuthUser               = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Realm                  = table.Column<string>(type: "TEXT", maxLength: 50,  nullable: true),
                    IgnoreTlsErrors        = table.Column<bool>  (type: "INTEGER", nullable: false),
                    PinnedCertThumbprint   = table.Column<string>(type: "TEXT", nullable: true),
                    SyncIntervalMinutes    = table.Column<int>   (type: "INTEGER", nullable: false),
                    LastSyncUtc            = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncError          = table.Column<string>(type: "TEXT", nullable: true),
                    RootGroupId            = table.Column<Guid>  (type: "TEXT", nullable: true),
                    DefaultProtocol        = table.Column<int>   (type: "INTEGER", nullable: false),
                    DefaultUsername        = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnabled              = table.Column<bool>  (type: "INTEGER", nullable: false),
                    CreatedAt              = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_ProxmoxSources", x => x.Id));

            migrationBuilder.AddColumn<Guid>(
                name: "ExternalSourceId",
                table: "Connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Connections",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManaged",
                table: "Connections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Connections_ExternalSourceId_ExternalId",
                table: "Connections",
                columns: new[] { "ExternalSourceId", "ExternalId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Connections_ProxmoxSources_ExternalSourceId",
                table: "Connections",
                column: "ExternalSourceId",
                principalTable: "ProxmoxSources",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Connections_ProxmoxSources_ExternalSourceId",
                table: "Connections");

            migrationBuilder.DropIndex(
                name: "IX_Connections_ExternalSourceId_ExternalId",
                table: "Connections");

            migrationBuilder.DropColumn(name: "ExternalSourceId", table: "Connections");
            migrationBuilder.DropColumn(name: "ExternalId",       table: "Connections");
            migrationBuilder.DropColumn(name: "IsManaged",        table: "Connections");

            migrationBuilder.DropTable(name: "ProxmoxSources");
        }
    }
}
