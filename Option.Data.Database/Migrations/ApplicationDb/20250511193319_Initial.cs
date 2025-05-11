using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Option.Data.Database.Migrations.ApplicationDb
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "OptionData",
                columns: table => new
                {
                    Strike = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Expiration = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CallOi = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    Iv = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    InstrumentName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PutOi = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    CallPrice = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    PutPrice = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    CallDelta = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    CallGamma = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    PutDelta = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    PutGamma = table.Column<double>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionData", x => new { x.Strike, x.Expiration, x.Type, x.Currency });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Currency",
                table: "OptionData",
                column: "Currency");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Expiration",
                table: "OptionData",
                column: "Expiration");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Strike",
                table: "OptionData",
                column: "Strike");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Type",
                table: "OptionData",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionData");
        }
    }
}
