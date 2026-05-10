using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusRDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSshAuthMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-connection SSH auth policy. 0 = Stored (legacy default
            // — every existing row gets this on upgrade so behaviour
            // doesn't change). 1 = ServerPrompt, 2 = PrivateKey,
            // 3 = KeyThenPrompt.
            migrationBuilder.AddColumn<int>(
                name: "SshAuthMode",
                table: "Connections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Absolute path to an OpenSSH-format private key file.
            // Used by PrivateKey + KeyThenPrompt modes; null otherwise.
            migrationBuilder.AddColumn<string>(
                name: "SshKeyFilePath",
                table: "Connections",
                type: "TEXT",
                nullable: true);

            // Credential-vault key for the key-file passphrase. Stored
            // separately from CredentialKey so a connection can have
            // BOTH a passphrase-protected key and a fallback password
            // (KeyThenPrompt mode).
            migrationBuilder.AddColumn<string>(
                name: "SshKeyPassphraseCredentialKey",
                table: "Connections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SshAuthMode",                   table: "Connections");
            migrationBuilder.DropColumn(name: "SshKeyFilePath",                table: "Connections");
            migrationBuilder.DropColumn(name: "SshKeyPassphraseCredentialKey", table: "Connections");
        }
    }
}
