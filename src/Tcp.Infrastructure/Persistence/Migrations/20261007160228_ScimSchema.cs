using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tcp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScimSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "idm");

            migrationBuilder.CreateTable(
                name: "ScimAudit",
                schema: "idm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClientId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ResourceType = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScimGroup",
                schema: "idm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    ExternalId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Created = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastModified = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScimUser",
                schema: "idm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    ExternalId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    GlobalUserId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    GivenName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    FamilyName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    PrimaryEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    EmailsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmailsSearch = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false, collation: "SQL_Latin1_General_CP1_CI_AS"),
                    Active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastModified = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimUser", x => x.Id);
                    table.CheckConstraint("CK_ScimUser_EmailsJson", "ISJSON([EmailsJson]) = 1");
                    table.CheckConstraint("CK_ScimUser_RawJson", "ISJSON([RawJson]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "ScimGroupMember",
                schema: "idm",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroupMember", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ScimGroupMember_ScimGroup_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "idm",
                        principalTable: "ScimGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScimGroupMember_ScimUser_UserId",
                        column: x => x.UserId,
                        principalSchema: "idm",
                        principalTable: "ScimUser",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScimAudit_At",
                schema: "idm",
                table: "ScimAudit",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroup_DisplayName",
                schema: "idm",
                table: "ScimGroup",
                column: "DisplayName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroupMember_UserId",
                schema: "idm",
                table: "ScimGroupMember",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_ExternalId",
                schema: "idm",
                table: "ScimUser",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_GlobalUserId",
                schema: "idm",
                table: "ScimUser",
                column: "GlobalUserId",
                unique: true,
                filter: "[GlobalUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_PrimaryEmail",
                schema: "idm",
                table: "ScimUser",
                column: "PrimaryEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_UserName",
                schema: "idm",
                table: "ScimUser",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScimAudit",
                schema: "idm");

            migrationBuilder.DropTable(
                name: "ScimGroupMember",
                schema: "idm");

            migrationBuilder.DropTable(
                name: "ScimGroup",
                schema: "idm");

            migrationBuilder.DropTable(
                name: "ScimUser",
                schema: "idm");
        }
    }
}
