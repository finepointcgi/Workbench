using Workbench.Contracts.Company;

namespace Workbench.Tests.CompanyTypes;

/// <summary>The terminology table: each company type's words, the fallback, and rewording whole sentences.</summary>
public sealed class CompanyTermsTests
{
    [Theory]
    [InlineData(CompanyType.GovernmentContractor, "contract", "CLIN", "funded value", "ceiling", "period of performance", "contracting officer")]
    [InlineData(CompanyType.ProfessionalServices, "agreement", "billing line", "authorized budget", "not-to-exceed", "term", "client contact")]
    [InlineData(CompanyType.EngineeringConstruction, "contract", "pay item", "authorized budget", "not-to-exceed", "contract duration", "owner's representative")]
    public void Each_company_type_has_its_own_words(CompanyType type, string contract, string clin, string funded, string ceiling, string pop, string officer)
    {
        var terms = CompanyTerms.For(type);

        Assert.Equal(type, terms.Type);
        Assert.Equal(contract, terms.Get("contract"));
        Assert.Equal(clin, terms.Get("clin"));
        Assert.Equal(funded, terms.Get("funded value"));
        Assert.Equal(ceiling, terms.Get("ceiling"));
        Assert.Equal(pop, terms.Get("period of performance"));
        Assert.Equal(officer, terms.Get("contracting officer"));
    }

    [Theory]
    [InlineData(CompanyType.GovernmentContractor)]
    [InlineData(CompanyType.ProfessionalServices)]
    [InlineData(CompanyType.EngineeringConstruction)]
    public void Customer_and_proposal_stay_the_same_everywhere(CompanyType type)
    {
        Assert.Equal("customer", CompanyTerms.For(type).Get("customer"));
        Assert.Equal("proposal", CompanyTerms.For(type).Get("proposal"));
    }

    [Fact]
    public void Unknown_keys_fall_back_to_the_key_itself()
    {
        Assert.Equal("widget", CompanyTerms.For(CompanyType.ProfessionalServices).Get("widget"));
        Assert.Equal("", CompanyTerms.Default.Get(""));
    }

    [Fact]
    public void An_unknown_company_type_uses_the_government_wording()
    {
        var terms = CompanyTerms.For((CompanyType)99);

        Assert.Equal("contract", terms.Get("contract"));
        Assert.Equal("CLIN", terms.Get("clin"));
    }

    [Fact]
    public void A_capitalised_key_gives_a_capitalised_word_and_acronyms_keep_their_case()
    {
        var prof = CompanyTerms.For(CompanyType.ProfessionalServices);

        Assert.Equal("Agreement", prof.Get("Contract"));
        Assert.Equal("Billing line", prof.Get("Clin"));
        Assert.Equal("CLIN", CompanyTerms.Default.Get("clin"));
    }

    [Fact]
    public void Every_row_has_words_for_all_three_types()
    {
        foreach (var key in CompanyTerms.Keys)
        {
            foreach (var type in Enum.GetValues<CompanyType>())
            {
                Assert.False(string.IsNullOrWhiteSpace(CompanyTerms.For(type).Get(key)), $"{key} / {type}");
            }
        }
    }

    [Fact]
    public void Reword_swaps_the_federal_phrases_in_a_sentence()
    {
        var prof = CompanyTerms.For(CompanyType.ProfessionalServices);

        Assert.Equal("Agreement C-100 has no authorized budget", prof.ReplaceTerms("Contract C-100 has no funded value"));
        Assert.Equal("Billing line 0001 is at 80% of its not-to-exceed", prof.ReplaceTerms("CLIN 0001 is at 80% of its ceiling"));
        Assert.Equal("Fund the billing lines", prof.ReplaceTerms("Fund the CLINs"));
        Assert.Equal("3 agreements have work ready to bill", prof.ReplaceTerms("3 contracts have work ready to bill"));
    }

    [Fact]
    public void Reword_leaves_a_government_contractors_text_and_look_alike_words_alone()
    {
        Assert.Equal("Contract C-100 has no funded value", CompanyTerms.Default.ReplaceTerms("Contract C-100 has no funded value"));
        Assert.Equal("Open the subcontracts", CompanyTerms.For(CompanyType.ProfessionalServices).ReplaceTerms("Open the subcontracts"));
        Assert.Equal("We won the award", CompanyTerms.For(CompanyType.ProfessionalServices).ReplaceTerms("We won the award"));
    }

    [Fact]
    public void Null_terms_read_as_the_government_wording()
    {
        ITerms? none = null;

        Assert.Equal("contract", none.Term("contract"));
        Assert.Equal("Contract", none.Reword("Contract"));
        Assert.False(none.IsCommercial());
        Assert.True(CompanyTerms.For(CompanyType.EngineeringConstruction).IsCommercial());
    }
}
