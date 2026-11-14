namespace Atlas.Server.Features.Assistant;
using Atlas.Server.Features.Assistant.Feedback;


/// <summary>Feedback and "Nahlásit chybějící obsah" of answers (plan phase 3 Q5). Needs <c>AddSearchGaps</c> (the report writes G2).</summary>
public static class AiFeedbackModule
{
    public static IServiceCollection AddAssistantFeedback(this IServiceCollection services) => services.AddScoped<AiFeedbackStore>();
}
