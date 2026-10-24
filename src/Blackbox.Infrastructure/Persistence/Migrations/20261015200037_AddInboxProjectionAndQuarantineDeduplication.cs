using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blackbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxProjectionAndQuarantineDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "deduplication_key",
                table: "dead_letter_messages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transport_message_id",
                table: "dead_letter_messages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_event_projections",
                columns: table => new
                {
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    event_count = table.Column<long>(type: "bigint", nullable: false),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_event_projections", x => x.workflow_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dead_letter_messages_deduplication_key",
                table: "dead_letter_messages",
                column: "deduplication_key",
                unique: true,
                filter: "deduplication_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_event_projections");

            migrationBuilder.DropIndex(
                name: "IX_dead_letter_messages_deduplication_key",
                table: "dead_letter_messages");

            migrationBuilder.DropColumn(
                name: "deduplication_key",
                table: "dead_letter_messages");

            migrationBuilder.DropColumn(
                name: "transport_message_id",
                table: "dead_letter_messages");
        }
    }
}
