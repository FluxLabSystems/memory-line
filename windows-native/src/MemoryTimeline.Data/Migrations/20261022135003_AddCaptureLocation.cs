using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MemoryTimeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptureLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "location_accuracy_m",
                table: "captures",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "location_latitude",
                table: "captures",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "location_longitude",
                table: "captures",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "location_accuracy_m",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "location_latitude",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "location_longitude",
                table: "captures");
        }
    }
}
