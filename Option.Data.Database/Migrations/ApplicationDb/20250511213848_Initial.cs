using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'1', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    CallOi = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    Strike = table.Column<int>(type: "integer", nullable: false),
                    Iv = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    InstrumentName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Expiration = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UnderlyingPrice = table.Column<double>(type: "double precision", nullable: false),
                    DeliveryPrice = table.Column<double>(type: "double precision", nullable: false),
                    MarkPrice = table.Column<double>(type: "double precision", nullable: false),
                    PutOi = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    CallPrice = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    PutPrice = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    CallDelta = table.Column<double>(type: "double precision", nullable: true),
                    CallGamma = table.Column<double>(type: "double precision", nullable: true),
                    PutDelta = table.Column<double>(type: "double precision", nullable: true),
                    PutGamma = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionData", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Currency",
                table: "OptionData",
                column: "Currency");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Currency_Type_Strike_Expiration",
                table: "OptionData",
                columns: new[] { "Currency", "Type", "Strike", "Expiration" });

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
