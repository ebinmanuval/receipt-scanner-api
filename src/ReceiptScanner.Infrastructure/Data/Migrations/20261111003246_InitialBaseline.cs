using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace ReceiptScanner.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "auth_user",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    password = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    last_login = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    is_superuser = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    username = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    first_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    last_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false),
                    is_staff = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    date_joined = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_user", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    bank_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    account_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    account_type = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    nickname = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    is_credit_card = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    dedupe_strategy = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ofx_statements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    server_date = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ledger_balance = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    ledger_balance_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    available_balance = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    file = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    original_filename = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    file_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    transactions_in_file = table.Column<uint>(type: "int unsigned", nullable: false),
                    transactions_created = table.Column<uint>(type: "int unsigned", nullable: false),
                    transactions_duplicate = table.Column<uint>(type: "int unsigned", nullable: false),
                    transactions_conflicted = table.Column<uint>(type: "int unsigned", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ofx_statements", x => x.id);
                    table.ForeignKey(
                        name: "FK_ofx_statements_bank_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "bank_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "category_rules",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    pattern = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    match_type = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    applies_to = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    category_id = table.Column<long>(type: "bigint", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    created_by = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_category_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_category_rules_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "receipts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    shop = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    total = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    purchased_on = table.Column<DateOnly>(type: "date", nullable: false),
                    client_uuid = table.Column<string>(type: "char(32)", maxLength: 32, nullable: true),
                    image = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    sheet_synced_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    needs_review = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    review_note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    no_bank_match = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    category_id = table.Column<long>(type: "bigint", nullable: true),
                    category_source = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    category_rule_id = table.Column<long>(type: "bigint", nullable: true),
                    category_confidence = table.Column<double>(type: "double", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_receipts_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receipts_category_rules_category_rule_id",
                        column: x => x.category_rule_id,
                        principalTable: "category_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bank_transactions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    statement_id = table.Column<long>(type: "bigint", nullable: true),
                    fitid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    dedupe_key = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    trntype = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    posted_on = table.Column<DateOnly>(type: "date", nullable: false),
                    user_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    memo = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    checknum = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    receipt_id = table.Column<long>(type: "bigint", nullable: true),
                    matched_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    match_method = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    match_confidence = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    match_note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    transfer_pair_id = table.Column<long>(type: "bigint", nullable: true),
                    paired_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    category_id = table.Column<long>(type: "bigint", nullable: true),
                    category_source = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    category_rule_id = table.Column<long>(type: "bigint", nullable: true),
                    category_confidence = table.Column<double>(type: "double", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_transactions", x => x.id);
                    table.ForeignKey(
                        name: "FK_bank_transactions_bank_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "bank_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_bank_transactions_bank_transactions_transfer_pair_id",
                        column: x => x.transfer_pair_id,
                        principalTable: "bank_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bank_transactions_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_transactions_category_rules_category_rule_id",
                        column: x => x.category_rule_id,
                        principalTable: "category_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bank_transactions_ofx_statements_statement_id",
                        column: x => x.statement_id,
                        principalTable: "ofx_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bank_transactions_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "receipt_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    receipt_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    qty = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: false),
                    line_total = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    category_id = table.Column<long>(type: "bigint", nullable: true),
                    category_source = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_receipt_items_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receipt_items_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "receipt_scans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    image = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    thumbnail = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    content_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    client_uuid = table.Column<string>(type: "char(32)", maxLength: 32, nullable: true),
                    captured_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    uploaded_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    uploaded_by_id = table.Column<int>(type: "int", nullable: true),
                    status = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    receipt_id = table.Column<long>(type: "bigint", nullable: true),
                    original_filename = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    byte_size = table.Column<uint>(type: "int unsigned", nullable: false),
                    width = table.Column<uint>(type: "int unsigned", nullable: true),
                    height = table.Column<uint>(type: "int unsigned", nullable: true),
                    image_format = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    extraction = table.Column<string>(type: "json", nullable: true),
                    extraction_check = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    extraction_notes = table.Column<string>(type: "json", nullable: false),
                    extraction_text = table.Column<string>(type: "longtext", nullable: false),
                    extraction_error = table.Column<string>(type: "longtext", nullable: false),
                    extraction_attempts = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    extraction_seconds = table.Column<double>(type: "double", nullable: true),
                    extraction_started_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    extracted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    extraction_task_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    reviewed_by_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_scans", x => x.id);
                    table.ForeignKey(
                        name: "FK_receipt_scans_auth_user_reviewed_by_id",
                        column: x => x.reviewed_by_id,
                        principalTable: "auth_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_receipt_scans_auth_user_uploaded_by_id",
                        column: x => x.uploaded_by_id,
                        principalTable: "auth_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_receipt_scans_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bank_transaction_conflicts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    statement_id = table.Column<long>(type: "bigint", nullable: true),
                    fields = table.Column<string>(type: "json", nullable: false),
                    stored = table.Column<string>(type: "json", nullable: false),
                    incoming = table.Column<string>(type: "json", nullable: false),
                    detected_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    resolution = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_transaction_conflicts", x => x.id);
                    table.ForeignKey(
                        name: "FK_bank_transaction_conflicts_bank_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "bank_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_bank_transaction_conflicts_ofx_statements_statement_id",
                        column: x => x.statement_id,
                        principalTable: "ofx_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "receipt_match_rejections",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    receipt_id = table.Column<long>(type: "bigint", nullable: false),
                    transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    rejected_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_match_rejections", x => x.id);
                    table.ForeignKey(
                        name: "FK_receipt_match_rejections_bank_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "bank_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_receipt_match_rejections_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_auth_user_username",
                table: "auth_user",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uniq_bank_account",
                table: "bank_accounts",
                columns: new[] { "bank_id", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_transaction_conflicts_resolved_at",
                table: "bank_transaction_conflicts",
                column: "resolved_at");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transaction_conflicts_statement_id",
                table: "bank_transaction_conflicts",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transaction_conflicts_transaction_id",
                table: "bank_transaction_conflicts",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_category_id",
                table: "bank_transactions",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_category_rule_id",
                table: "bank_transactions",
                column: "category_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_category_source",
                table: "bank_transactions",
                column: "category_source");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_dedupe_key",
                table: "bank_transactions",
                column: "dedupe_key");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_receipt_id",
                table: "bank_transactions",
                column: "receipt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_statement_id",
                table: "bank_transactions",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_transfer_pair_id",
                table: "bank_transactions",
                column: "transfer_pair_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "bank_transa_account_d9e0db_idx",
                table: "bank_transactions",
                columns: new[] { "account_id", "posted_on" });

            migrationBuilder.CreateIndex(
                name: "bank_transa_posted__5529ce_idx",
                table: "bank_transactions",
                columns: new[] { "posted_on", "amount" });

            migrationBuilder.CreateIndex(
                name: "uniq_account_dedupe_key",
                table: "bank_transactions",
                columns: new[] { "account_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_categories_name",
                table: "categories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_categories_parent_id",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_category_rules_category_id",
                table: "category_rules",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "uniq_category_rule",
                table: "category_rules",
                columns: new[] { "pattern", "match_type", "applies_to" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ofx_statements_account_id",
                table: "ofx_statements",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_ofx_statements_file_hash",
                table: "ofx_statements",
                column: "file_hash");

            migrationBuilder.CreateIndex(
                name: "ofx_stateme_start_d_7ef5c2_idx",
                table: "ofx_statements",
                columns: new[] { "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "IX_receipt_items_category_id",
                table: "receipt_items",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_items_receipt_id",
                table: "receipt_items",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "receipt_ite_name_b17b8f_idx",
                table: "receipt_items",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_match_rejections_transaction_id",
                table: "receipt_match_rejections",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "uniq_match_rejection",
                table: "receipt_match_rejections",
                columns: new[] { "receipt_id", "transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_captured_at",
                table: "receipt_scans",
                column: "captured_at");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_client_uuid",
                table: "receipt_scans",
                column: "client_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_content_hash",
                table: "receipt_scans",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_receipt_id",
                table: "receipt_scans",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_reviewed_by_id",
                table: "receipt_scans",
                column: "reviewed_by_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_status",
                table: "receipt_scans",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_uploaded_at",
                table: "receipt_scans",
                column: "uploaded_at");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_scans_uploaded_by_id",
                table: "receipt_scans",
                column: "uploaded_by_id");

            migrationBuilder.CreateIndex(
                name: "receipt_sca_status_fab29b_idx",
                table: "receipt_scans",
                columns: new[] { "status", "uploaded_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_receipts_category_id",
                table: "receipts",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_category_rule_id",
                table: "receipts",
                column: "category_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_category_source",
                table: "receipts",
                column: "category_source");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_client_uuid",
                table: "receipts",
                column: "client_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipts_needs_review",
                table: "receipts",
                column: "needs_review");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_no_bank_match",
                table: "receipts",
                column: "no_bank_match");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_sheet_synced_at",
                table: "receipts",
                column: "sheet_synced_at");

            migrationBuilder.CreateIndex(
                name: "receipts_purchas_144f66_idx",
                table: "receipts",
                column: "purchased_on");

            migrationBuilder.CreateIndex(
                name: "receipts_shop_1c0884_idx",
                table: "receipts",
                column: "shop");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_transaction_conflicts");

            migrationBuilder.DropTable(
                name: "receipt_items");

            migrationBuilder.DropTable(
                name: "receipt_match_rejections");

            migrationBuilder.DropTable(
                name: "receipt_scans");

            migrationBuilder.DropTable(
                name: "bank_transactions");

            migrationBuilder.DropTable(
                name: "auth_user");

            migrationBuilder.DropTable(
                name: "ofx_statements");

            migrationBuilder.DropTable(
                name: "receipts");

            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropTable(
                name: "category_rules");

            migrationBuilder.DropTable(
                name: "categories");
        }
    }
}
