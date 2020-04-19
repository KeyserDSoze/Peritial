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

        public virtual DbSet<TableContributor> TableContributor { get; set; }
        public virtual DbSet<TableCountry> TableCountry { get; set; }
        public virtual DbSet<TableCustomer> TableCustomer { get; set; }
        public virtual DbSet<TableExpertise> TableExpertise { get; set; }
        public virtual DbSet<TableExternalAccount> TableExternalAccount { get; set; }
        public virtual DbSet<TableInspectorate> TableInspectorate { get; set; }
        public virtual DbSet<TableInsurance> TableInsurance { get; set; }
        public virtual DbSet<TableInsuranceAgency> TableInsuranceAgency { get; set; }
        public virtual DbSet<TableInsuranceCompany> TableInsuranceCompany { get; set; }
        public virtual DbSet<TableLocation> TableLocation { get; set; }
        public virtual DbSet<TableMember> TableMember { get; set; }
        public virtual DbSet<TableMessages> TableMessages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TableContributor>(entity =>
            {
                entity.HasOne(d => d.Contributor)
                    .WithMany(p => p.TableContributorContributor)
                    .HasForeignKey(d => d.ContributorId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Contributor_Table_Member");

                entity.HasOne(d => d.Member)
                    .WithMany(p => p.TableContributorMember)
                    .HasForeignKey(d => d.MemberId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Contributor_Table_Member1");
            });

            modelBuilder.Entity<TableCountry>(entity =>
            {
                entity.Property(e => e.Label).IsUnicode(false);
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

            modelBuilder.Entity<TableExpertise>(entity =>
            {
                entity.Property(e => e.AccidentId).IsUnicode(false);

                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.HasOne(d => d.Contributor)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.ContributorId)
                    .HasConstraintName("FK_Table_Expertise_Table_Contributor");

                entity.HasOne(d => d.CounterPart)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.CounterPartId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_Customer1");

                entity.HasOne(d => d.Inspectorate)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.InspectorateId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_Inspectorate");

                entity.HasOne(d => d.InsuranceAgency)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.InsuranceAgencyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_InsuranceAgency");

                entity.HasOne(d => d.Insurance)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.InsuranceId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_Insurance");

                entity.HasOne(d => d.Member)
                    .WithMany(p => p.TableExpertise)
                    .HasForeignKey(d => d.MemberId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Expertise_Table_Member");
            });

            modelBuilder.Entity<TableExternalAccount>(entity =>
            {
                entity.Property(e => e.Passowrd).IsUnicode(false);

                entity.Property(e => e.Username).IsUnicode(false);

                entity.HasOne(d => d.Member)
                    .WithMany(p => p.TableExternalAccount)
                    .HasForeignKey(d => d.MemberId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_ExternalAccount_Table_Member");
            });

            modelBuilder.Entity<TableInspectorate>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableInspectorate)
                    .HasForeignKey(d => d.AddressId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Inspectorate_Table_Location");

                entity.HasOne(d => d.InsuranceCompany)
                    .WithMany(p => p.TableInspectorate)
                    .HasForeignKey(d => d.InsuranceCompanyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Inspectorate_Table_InsuranceCompany");
            });

            modelBuilder.Entity<TableInsurance>(entity =>
            {
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.HasOne(d => d.Customer)
                    .WithMany(p => p.TableInsurance)
                    .HasForeignKey(d => d.CustomerId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Insurance_Table_Customer");

                entity.HasOne(d => d.InsuranceCompany)
                    .WithMany(p => p.TableInsurance)
                    .HasForeignKey(d => d.InsuranceCompanyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Insurance_Table_InsuranceCompany");
            });

            modelBuilder.Entity<TableInsuranceAgency>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableInsuranceAgency)
                    .HasForeignKey(d => d.AddressId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_InsuranceAgency_Table_Location");

                entity.HasOne(d => d.InsuranceCompany)
                    .WithMany(p => p.TableInsuranceAgency)
                    .HasForeignKey(d => d.InsuranceCompanyId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_InsuranceAgency_Table_InsuranceCompany");
            });

            modelBuilder.Entity<TableInsuranceCompany>(entity =>
            {
                entity.Property(e => e.ExternalId).IsUnicode(false);

                entity.Property(e => e.Label).IsUnicode(false);

                entity.HasOne(d => d.Address)
                    .WithMany(p => p.TableInsuranceCompany)
                    .HasForeignKey(d => d.AddressId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_InsuranceCompany_Table_Location");
            });

            modelBuilder.Entity<TableLocation>(entity =>
            {
                entity.Property(e => e.Address).IsUnicode(false);

                entity.Property(e => e.City).IsUnicode(false);

                entity.Property(e => e.PostalCode).IsUnicode(false);

                entity.HasOne(d => d.Country)
                    .WithMany(p => p.TableLocation)
                    .HasForeignKey(d => d.CountryId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Location_Table_Country");
            });

            modelBuilder.Entity<TableMember>(entity =>
            {
                entity.Property(e => e.Name).IsUnicode(false);

                entity.Property(e => e.Title).IsUnicode(false);
            });

            modelBuilder.Entity<TableMessages>(entity =>
            {
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Value).IsUnicode(false);

                entity.HasOne(d => d.Expertise)
                    .WithMany(p => p.TableMessages)
                    .HasForeignKey(d => d.ExpertiseId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Table_Messages_Table_Expertise");
            });

            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
