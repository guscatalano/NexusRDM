using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusRDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIconColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IconColorHex",
                table: "Connections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IconColorHex",
                table: "Connections");
        }
    }
}
