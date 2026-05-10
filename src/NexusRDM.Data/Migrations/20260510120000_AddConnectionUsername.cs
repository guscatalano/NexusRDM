using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusRDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Plaintext username on the profile. SSH protocol needs the
            // username in the first auth packet, before keyboard-
            // interactive can fire, so a connection in ServerPrompt
            // mode can't pull it from the credential vault (vault
            // always pairs user+pass). Storing it here lets
            // ServerPrompt connections skip the credential dialog
            // entirely. RDP connections are free to use it too —
            // it's just a default that overrides what's in the vault.
            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "Connections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Username", table: "Connections");
        }
    }
}
