using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Expertiser.Database.Models
{
    public partial class ContextExpertiser : DbContext
    {
        public ContextExpertiser()
        {
        }

        public ContextExpertiser(DbContextOptions<ContextExpertiser> options)
            : base(options)
        {
        }

        public virtual DbSet<AspNetRoleClaims> AspNetRoleClaims { get; set; }
        public virtual DbSet<AspNetRoles> AspNetRoles { get; set; }
        public virtual DbSet<AspNetUserClaims> AspNetUserClaims { get; set; }
        public virtual DbSet<AspNetUserLogins> AspNetUserLogins { get; set; }
        public virtual DbSet<AspNetUserRoles> AspNetUserRoles { get; set; }
        public virtual DbSet<AspNetUserTokens> AspNetUserTokens { get; set; }
        public virtual DbSet<AspNetUsers> AspNetUsers { get; set; }
        public virtual DbSet<TableAccident> TableAccident { get; set; }
        public virtual DbSet<TableBill> TableBill { get; set; }
        public virtual DbSet<TableBranch> TableBranch { get; set; }
        public virtual DbSet<TableBranchType> TableBranchType { get; set; }
        public virtual DbSet<TableCity> TableCity { get; set; }
        public virtual DbSet<TableCountry> TableCountry { get; set; }
        public virtual DbSet<TableCustomer> TableCustomer { get; set; }
        public virtual DbSet<TableDamaged> TableDamaged { get; set; }
        public virtual DbSet<TableExpertise> TableExpertise { get; set; }
        public virtual DbSet<TableInspectorate> TableInspectorate { get; set; }
        public virtual DbSet<TableInsurance> TableInsurance { get; set; }
        public virtual DbSet<TableInsuranceAdjuster> TableInsuranceAdjuster { get; set; }
        public virtual DbSet<TableInsuranceAgency> TableInsuranceAgency { get; set; }
        public virtual DbSet<TableInsuranceCompany> TableInsuranceCompany { get; set; }
        public virtual DbSet<TableLocation> TableLocation { get; set; }
        public virtual DbSet<TableMessage> TableMessage { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AspNetRoleClaims>(entity =>
            {
                entity.HasIndex(e => e.RoleId);
            });

            modelBuilder.Entity<AspNetRoles>(entity =>
            {
                entity.HasIndex(e => e.NormalizedName)
                    .HasName("RoleNameIndex")
                    .IsUnique()
                    .HasFilter("([NormalizedName] IS NOT NULL)");
            });

            modelBuilder.Entity<AspNetUserClaims>(entity =>
            {
                entity.HasIndex(e => e.UserId);
            });

            modelBuilder.Entity<AspNetUserLogins>(entity =>
            {
                entity.HasKey(e => new { e.LoginProvider, e.ProviderKey });

                entity.HasIndex(e => e.UserId);
            });

            modelBuilder.Entity<AspNetUserRoles>(entity =>
            {
                entity.HasKey(e => new { e.UserId, e.RoleId });

                entity.HasIndex(e => e.RoleId);
            });

            modelBuilder.Entity<AspNetUserTokens>(entity =>
            {
                entity.HasKey(e => new { e.UserId, e.LoginProvider, e.Name });
            });

            modelBuilder.Entity<AspNetUsers>(entity =>
            {
                entity.HasIndex(e => e.NormalizedEmail)
                    .HasName("EmailIndex");

                entity.HasIndex(e => e.NormalizedUserName)
                    .HasName("UserNameIndex")
                    .IsUnique()
                    .HasFilter("([NormalizedUserName] IS NOT NULL)");

                entity.Property(e => e.Alias).IsUnicode(false);

                entity.Property(e => e.Name).IsUnicode(false);

                entity.Property(e => e.Surname).IsUnicode(false);
            });

            modelBuilder.Entity<TableAccident>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableAccident)
                    .HasForeignKey(d => d.AddressId)
                    .HasConstraintName("FK_Table_Accident_Table_Location");

                entity.HasOne(d => d.Inspectorate)
                    .WithMany(p => p.TableAccident)
                    .HasForeignKey(d => d.InspectorateId)
                    .HasConstraintName("FK_Table_Accident_Table_Inspectorate");

                entity.HasOne(d => d.InsuranceAdjuster)
                    .WithMany(p => p.TableAccident)
                    .HasForeignKey(d => d.InsuranceAdjusterId)
                    .HasConstraintName("FK_Table_Accident_Table_InsuranceAdjuster");

                entity.HasOne(d => d.InsuranceAgency)
                    .WithMany(p => p.TableAccident)
                    .HasForeignKey(d => d.InsuranceAgencyId)
                    .HasConstraintName("FK_Table_Accident_Table_InsuranceAgency");

                entity.HasOne(d => d.Insurance)
                    .WithMany(p => p.TableAccident)
                    .HasForeignKey(d => d.InsuranceId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Accident_Table_Insurance");
            });

            modelBuilder.Entity<TableBill>(entity =>
            {
                entity.HasOne(d => d.Expertise)
                    .WithMany(p => p.TableBill)
                    .HasForeignKey(d => d.ExpertiseId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Bill_Table_Expertise");
            });

            modelBuilder.Entity<TableBranch>(entity =>
            {
                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.BranchType)
                    .WithMany(p => p.TableBranch)
                    .HasForeignKey(d => d.BranchTypeId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Branch_Table_BranchType");
            });

            modelBuilder.Entity<TableBranchType>(entity =>
            {
                entity.Property(e => e.Label).IsUnicode(false);
            });

            modelBuilder.Entity<TableCity>(entity =>
            {
                entity.Property(e => e.Name).IsUnicode(false);

                entity.Property(e => e.PostalCode).IsUnicode(false);

                entity.Property(e => e.Province).IsUnicode(false);

                entity.Property(e => e.ProvinceAbbreviation).IsFixedLength();

                entity.Property(e => e.Region).IsUnicode(false);

                entity.HasOne(d => d.Country)
                    .WithMany(p => p.TableCity)
                    .HasForeignKey(d => d.CountryId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_City_Table_Country");
            });

            modelBuilder.Entity<TableCountry>(entity =>
            {
                entity.Property(e => e.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<TableCustomer>(entity =>
            {
                entity.Property(e => e.FiscalCode).IsUnicode(false);

                entity.Property(e => e.Name).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableCustomer)
                    .HasForeignKey(d => d.AddressId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Customer_Table_Location");
            });

            modelBuilder.Entity<TableDamaged>(entity =>
            {
                entity.HasKey(e => new { e.AccidentId, e.CustomerId });

                entity.HasOne(d => d.Accident)
                    .WithMany(p => p.TableDamaged)
                    .HasForeignKey(d => d.AccidentId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Damaged_Table_Accident");

                entity.HasOne(d => d.Customer)
                    .WithMany(p => p.TableDamaged)
                    .HasForeignKey(d => d.CustomerId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Damaged_Table_Customer");
            });

            modelBuilder.Entity<TableExpertise>(entity =>
            {
                entity.Property(e => e.FileId).IsUnicode(false);

                entity.HasOne(d => d.Accident)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.AccidentId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_Accident");

                entity.HasOne(d => d.Branch)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.BranchId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_Branch");

                entity.HasOne(d => d.Contributor)
                    .WithMany(p => p.TableExpertiseContributor)
                    .HasForeignKey(d => d.ContributorId)
                    .HasConstraintName("FK_Table_Expertise_AspNetUsers1");

                entity.HasOne(d => d.Member)
                    .WithMany(p => p.TableExpertiseMember)
                    .HasForeignKey(d => d.MemberId)
                    .HasConstraintName("FK_Table_Expertise_AspNetUsers");
            });

            modelBuilder.Entity<TableInspectorate>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableInspectorate)
                    .HasForeignKey(d => d.AddressId)
                    .HasConstraintName("FK_Table_Inspectorate_Table_Location");

                entity.HasOne(d => d.InsuranceCompany)
                    .WithMany(p => p.TableInspectorate)
                    .HasForeignKey(d => d.InsuranceCompanyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Inspectorate_Table_InsuranceCompany");
            });

            modelBuilder.Entity<TableInsurance>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.HasOne(d => d.Contractor)
                    .WithMany(p => p.TableInsuranceContractor)
                    .HasForeignKey(d => d.ContractorId)
                    .HasConstraintName("FK_Table_Insurance_Table_Customer1");

                entity.HasOne(d => d.Customer)
                    .WithMany(p => p.TableInsuranceCustomer)
                    .HasForeignKey(d => d.CustomerId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Insurance_Table_Customer");

                entity.HasOne(d => d.InsuranceCompany)
                    .WithMany(p => p.TableInsurance)
                    .HasForeignKey(d => d.InsuranceCompanyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Insurance_Table_InsuranceCompany");
            });

            modelBuilder.Entity<TableInsuranceAdjuster>(entity =>
            {
                entity.Property(e => e.Name).IsUnicode(false);
            });

            modelBuilder.Entity<TableInsuranceAgency>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableInsuranceAgency)
                    .HasForeignKey(d => d.AddressId)
                    .HasConstraintName("FK_Table_InsuranceAgency_Table_Location");

                entity.HasOne(d => d.InsuranceCompany)
                    .WithMany(p => p.TableInsuranceAgency)
                    .HasForeignKey(d => d.InsuranceCompanyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_InsuranceAgency_Table_InsuranceCompany");
            });

            modelBuilder.Entity<TableInsuranceCompany>(entity =>
            {
                entity.Property(e => e.Alias).IsUnicode(false);

                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableInsuranceCompany)
                    .HasForeignKey(d => d.AddressId)
                    .HasConstraintName("FK_Table_InsuranceCompany_Table_Location");
            });

            modelBuilder.Entity<TableLocation>(entity =>
            {
                entity.Property(e => e.Address).IsUnicode(false);

                entity.Property(e => e.PostalCode).IsUnicode(false);

                entity.HasOne(d => d.City)
                    .WithMany(p => p.TableLocation)
                    .HasForeignKey(d => d.CityId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Location_Table_City");
            });

            modelBuilder.Entity<TableMessage>(entity =>
            {
                entity.Property(e => e.Value).IsUnicode(false);

                entity.HasOne(d => d.Expertise)
                    .WithMany(p => p.TableMessage)
                    .HasForeignKey(d => d.ExpertiseId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Messages_Table_Expertise");

                entity.HasOne(d => d.User)
                    .WithMany(p => p.TableMessage)
                    .HasForeignKey(d => d.UserId)
                    .HasConstraintName("FK_Table_Messages_AspNetUsers");
            });

            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
