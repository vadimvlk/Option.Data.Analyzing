using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

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
                name: "CurrencyType",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptionType",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptionData",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'1', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OpenInterest = table.Column<double>(type: "double precision", nullable: false),
                    Strike = table.Column<int>(type: "integer", nullable: false),
                    Iv = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    OptionTypeId = table.Column<int>(type: "integer", nullable: false),
                    CurrencyTypeId = table.Column<int>(type: "integer", nullable: false),
                    InstrumentName = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    Expiration = table.Column<string>(type: "citext", maxLength: 50, nullable: false),
                    UnderlyingPrice = table.Column<double>(type: "double precision", nullable: false),
                    DeliveryPrice = table.Column<double>(type: "double precision", nullable: false),
                    MarkPrice = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    Delta = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    Gamma = table.Column<double>(type: "numeric(18,8)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionData", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptionData_CurrencyType_CurrencyTypeId",
                        column: x => x.CurrencyTypeId,
                        principalTable: "CurrencyType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OptionData_OptionType_OptionTypeId",
                        column: x => x.OptionTypeId,
                        principalTable: "OptionType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CurrencyType",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "BTC" },
                    { 2, "ETH" }
                });

            migrationBuilder.InsertData(
                table: "OptionType",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "Call" },
                    { 2, "Put" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_CurrencyTypeId",
                table: "OptionData",
                column: "CurrencyTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_CurrencyTypeId_OptionTypeId_Strike_Expiration",
                table: "OptionData",
                columns: new[] { "CurrencyTypeId", "OptionTypeId", "Strike", "Expiration" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Expiration",
                table: "OptionData",
                column: "Expiration");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_OptionTypeId",
                table: "OptionData",
                column: "OptionTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_OptionData_Strike",
                table: "OptionData",
                column: "Strike");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionData");

            migrationBuilder.DropTable(
                name: "CurrencyType");

            migrationBuilder.DropTable(
                name: "OptionType");
        }
    }
}
