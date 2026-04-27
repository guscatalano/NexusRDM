using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusRDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIconGlyph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IconGlyph",
                table: "Connections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IconGlyph",
                table: "Connections");
        }
    }
}
