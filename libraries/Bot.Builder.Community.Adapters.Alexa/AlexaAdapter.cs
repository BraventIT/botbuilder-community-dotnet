﻿namespace Bot.Builder.Community.Adapters.Alexa
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Bot.Builder.Community.Adapters.Alexa.Directives;
    using Microsoft.Bot.Builder;
    using Microsoft.Bot.Schema;
    using Newtonsoft.Json;

    public class AlexaAdapter : BotAdapter
    {
        // Waiting time between messages
        private const int LongTimeBreak = 1;

        private const string ShortTimeBreak = "0.5";

        private Dictionary<string, List<Activity>> Responses { get; set; }

        public bool ShouldEndSessionByDefault { get; set; }

        public bool ConvertBotBuilderCardsToAlexaCards { get; set; }

        public string DirectivesBackgroundImageByDefault { get; set; }

        public static string DirectivesBackgroundImageByDefaultNoStatic;

        public string TittleTextByDefault { get; set; }

        public AlexaAdapter()
        {
            ShouldEndSessionByDefault = true;
            ConvertBotBuilderCardsToAlexaCards = false;
        }

        /// <summary>
        /// Adds middleware to the adapter's pipeline.
        /// </summary>
        public new AlexaAdapter Use(IMiddleware middleware)
        {
            MiddlewareSet.Use(middleware);
            return this;
        }

        public async Task<AlexaResponseBody> ProcessActivity(AlexaRequestBody alexaRequest, BotCallbackHandler callback)
        {
            TurnContext context = null;

            try
            {
                var activity = RequestToActivity(alexaRequest);
                BotAssert.ActivityNotNull(activity);

                context = new TurnContext(this, activity);

                if (alexaRequest.Session.Attributes != null && alexaRequest.Session.Attributes.Any())
                {
                    context.TurnState.Add("AlexaSessionAttributes", alexaRequest.Session.Attributes);
                }
                else
                {
                    context.TurnState.Add("AlexaSessionAttributes", new Dictionary<string, string>());
                }

                context.TurnState.Add("AlexaResponseDirectives", new List<IAlexaDirective>());

                Responses = new Dictionary<string, List<Activity>>();

                await base.RunPipelineAsync(context, callback, default(CancellationToken)).ConfigureAwait(false);

                var key = $"{activity.Conversation.Id}:{activity.Id}";

                try
                {
                    AlexaResponseBody response = null;
                    var activities = Responses.ContainsKey(key) ? Responses[key] : new List<Activity>();
                    response = CreateResponseFromLastActivity(activities, context);
                    response.SessionAttributes = context.AlexaSessionAttributes();
                    return response;
                }
                finally
                {
                    if (Responses.ContainsKey(key))
                    {
                        Responses.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                await this.OnTurnError(context, ex);
                throw;
            }
        }

        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken CancellationToken)
        {
            var resourceResponses = new List<ResourceResponse>();

            foreach (var activity in activities)
            {
                switch (activity.Type)
                {
                    case ActivityTypes.Message:
                    case ActivityTypes.EndOfConversation:
                        var conversation = activity.Conversation ?? new ConversationAccount();
                        var key = $"{conversation.Id}:{activity.ReplyToId}";

                        if (Responses.ContainsKey(key))
                        {
                            Responses[key].Add(activity);
                        }
                        else
                        {
                            Responses[key] = new List<Activity> { activity };
                        }

                        break;
                    default:
                        Trace.WriteLine(
                            $"AlexaAdapter.SendActivities(): Activities of type '{activity.Type}' aren't supported.");
                        break;
                }

                resourceResponses.Add(new ResourceResponse(activity.Id));
            }

            return Task.FromResult(resourceResponses.ToArray());
        }

        private static Activity RequestToActivity(AlexaRequestBody skillRequest)
        {
            var system = skillRequest.Context.System;

            var activity = new Activity
            {
                ChannelId = "alexa",
                ServiceUrl = $"{system.ApiEndpoint}?token ={system.ApiAccessToken}",
                Recipient = new ChannelAccount(system.Application.ApplicationId, "skill"),
                From = new ChannelAccount(system.User.UserId, "user"),
                Conversation = new ConversationAccount(false, "conversation", skillRequest.Session.SessionId),
                Type = skillRequest.Request.Type,
                Id = skillRequest.Request.RequestId,
                Timestamp = DateTime.ParseExact(skillRequest.Request.Timestamp, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                Locale = skillRequest.Request.Locale
            };

            switch (activity.Type)
            {
                case AlexaRequestTypes.IntentRequest:
                    activity.Value = (skillRequest.Request as AlexaIntentRequest)?.Intent;
                    activity.Code = (skillRequest.Request as AlexaIntentRequest)?.DialogState.ToString();
                    break;
                case AlexaRequestTypes.SessionEndedRequest:
                    activity.Code = (skillRequest.Request as AlexaSessionEndRequest)?.Reason;
                    activity.Value = (skillRequest.Request as AlexaSessionEndRequest)?.Error;
                    break;
            }

            activity.ChannelData = skillRequest;

            return activity;
        }

        private AlexaResponseBody CreateResponseFromLastActivity(IEnumerable<Activity> activities, ITurnContext context)
        {
            var response = new AlexaResponseBody()
            {
                Version = "1.0",
                Response = new AlexaResponse()
                {
                    ShouldEndSession = context.GetAlexaRequestBody().Request.Type ==
                                       AlexaRequestTypes.SessionEndedRequest
                                       || ShouldEndSessionByDefault
                }
            };

            if (string.IsNullOrEmpty(context.GetAlexaRequestBody().Request.Type))
            {
                response.Response.ShouldEndSession = null;
            }

            if (context.GetAlexaRequestBody().Request.Type == AlexaRequestTypes.SessionEndedRequest
                && (activities == null || !activities.Any()))
            {
                response.Response.OutputSpeech = new AlexaOutputSpeech()
                {
                    Type = AlexaOutputSpeechType.PlainText,
                    Text = string.Empty
                };
                return response;
            }

            #region ECA_Code

            DirectivesBackgroundImageByDefaultNoStatic = this.DirectivesBackgroundImageByDefault;

            // Implement multiple parser of activities
            response.Response.OutputSpeech = new AlexaOutputSpeech { Text = string.Empty, Ssml = string.Empty };
            foreach (var activity in activities)
            {
                if (activity.Type == ActivityTypes.EndOfConversation)
                {
                    response.Response.ShouldEndSession = true;
                }

                if (!string.IsNullOrEmpty(activity.Speak))
                {
                    SetResponseSSML(ref response, activity.Speak);

                    if (!string.IsNullOrEmpty(activity.Text))
                    {
                        SetResponseText(ref response, activity.Text);
                    }
                }
                else if (!string.IsNullOrEmpty(activity.Text))
                {

                    SetResponseSSML(ref response, activity.Text);
                    SetResponseText(ref response, activity.Text);
                }

                if (context.TurnState.ContainsKey("AlexaReprompt"))
                {
                    var repromptSpeech = context.TurnState.Get<string>("AlexaReprompt");

                    response.Response.Reprompt = new Reprompt()
                    {
                        OutputSpeech = new AlexaOutputSpeech()
                        {
                            Type = AlexaOutputSpeechType.SSML,
                            Ssml = repromptSpeech
                        }
                    };
                }

                AddDirectivesToResponse(context, response);

                AddCardToResponse(context, response, activity);

                switch (activity.InputHint)
                {
                    case InputHints.IgnoringInput:
                        response.Response.ShouldEndSession = true;
                        break;
                    case InputHints.ExpectingInput:
                        response.Response.ShouldEndSession = false;
                        break;
                    case InputHints.AcceptingInput:
                    default:
                        break;
                }
            }

            // Parse Text message to directives with defautl Background
            if (response.Response.Card == null && response.Response.Directives.Count() <= 0)
            {
                Image backgroundImage = new Image();
                if (!string.IsNullOrEmpty(DirectivesBackgroundImageByDefault))
                {
                    backgroundImage = new Image
                    {
                        Sources = new ImageSource[]
                        {
                            new ImageSource{Url = DirectivesBackgroundImageByDefault}
                        }
                    };
                }

                DisplayDirective directive = new DisplayDirective()
                {
                    Template = new DisplayRenderBodyTemplate1()
                    {
                        Title = TittleTextByDefault,
                        TextContent = new TextContent
                        {
                            TertiaryText = new InnerTextContent
                            {
                                // centered text
                                Text = $"<br/><br/><br/><div align='center'><font size=\"5\"><b>{response.Response.OutputSpeech.Text.Replace("\n", "<br/>")}</b></font></div>",
                                Type = TextContentType.RichText
                            }
                        },
                        BackgroundImage = backgroundImage
                    }
                };

                context.AlexaResponseDirectives().Add(directive);
                AddDirectivesToResponse(context, response);
            }
            else
            {
                // if we have directives we add the text to them.
                if (response.Response.Directives.Any())
                {
                    // Get first Directive.
                    var directive = (DisplayDirective)response.Response.Directives.First();

                    var bodyTemplate1 = new DisplayRenderBodyTemplate1();
                    if (directive.Template.Type.Equals(bodyTemplate1.Type))
                    {
                        var renderBody = (DisplayRenderBodyTemplate1)directive.Template;

                        if (string.IsNullOrEmpty(renderBody.Title))
                        {
                            renderBody.Title = this.TittleTextByDefault;
                        }

                        // we always try to added at TertiaryText If it've text we added a '\n' and next the text.
                        if (renderBody.TextContent.PrimaryText != null)
                        {
                            SetResponseSSML(ref response, renderBody.TextContent.PrimaryText.Text);
                        }

                        renderBody.TextContent.PrimaryText = new InnerTextContent { Text = response.Response.OutputSpeech.Text };

                        directive.Template = renderBody;
                        response.Response.Directives = new List<DisplayDirective> { directive }.ToArray();
                    }

                    var bodyTemplate2 = new DisplayRenderBodyTemplate2();
                    if (directive.Template.Type.Equals(bodyTemplate2.Type))
                    {
                        var renderBody = (DisplayRenderBodyTemplate2)directive.Template;

                        if (string.IsNullOrEmpty(renderBody.Title))
                        {
                            renderBody.Title = this.TittleTextByDefault;
                        }

                        // we always try to added at TertiaryText If it've text we added a '\n' and next the text.
                        if (renderBody.TextContent.TertiaryText != null)
                        {
                            SetResponseSSML(ref response, renderBody.TextContent.TertiaryText.Text);
                        }
                    }

                    var bodyTemplateList2 = new DisplayRenderListTemplate2();
                    if (directive.Template.Type.Equals(bodyTemplateList2.Type))
                    {
                        var renderBody = (DisplayRenderListTemplate2)directive.Template;

                        // Add to SSML the options of list
                        foreach (var item in renderBody.ListItems)
                        {
                            response.Response.OutputSpeech.Ssml = response.Response.OutputSpeech.Ssml + $"  <break time=\"{ShortTimeBreak}s\"/>" + item.TextContent.PrimaryText.Text;
                        }

                        // Add a default backfround if dont exist one
                        if (renderBody.BackgroundImage == null || renderBody.BackgroundImage.Sources.Count() < 0)
                        {
                            if (!string.IsNullOrEmpty(this.DirectivesBackgroundImageByDefault))
                            {
                                renderBody.BackgroundImage = new Image
                                {
                                    Sources = new List<ImageSource>
                                    {
                                        new ImageSource
                                        {
                                            Url = this.DirectivesBackgroundImageByDefault
                                        }
                                    }.ToArray()
                                };
                            }
                        }

                        if (string.IsNullOrEmpty(renderBody.Title))
                        {
                            renderBody.Title = TittleTextByDefault;
                        }

                        directive.Template = renderBody;
                        response.Response.Directives = new List<DisplayDirective> { directive }.ToArray();
                    }
                }
                else
                {
                    SetResponseText(ref response, response.Response.Card.Text);

                    // if we have cards we add the text to them.
                    response.Response.Card.Text = response.Response.Card.Text.Contains(response.Response.OutputSpeech.Text)
                        ? response.Response.Card.Text
                        : response.Response.OutputSpeech.Text;
                }
            }

            if (response.Response.OutputSpeech.Type.Equals(AlexaOutputSpeechType.SSML))
            {
                response.Response.OutputSpeech.Ssml = $"<speak>{response.Response.OutputSpeech.Ssml}</speak> ";
            }

            #endregion
            return response;
        }

        private void AddCardToResponse(ITurnContext context, AlexaResponseBody response, Activity activity)
        {
            if (activity.Attachments != null
                && activity.Attachments.Any(a => a.ContentType == SigninCard.ContentType))
            {
                response.Response.Card = new AlexaCard()
                {
                    Type = AlexaCardType.LinkAccount
                };
            }
            else
            {
                if (context.TurnState.ContainsKey("AlexaCard") && context.TurnState["AlexaCard"] is AlexaCard)
                {
                    response.Response.Card = context.TurnState.Get<AlexaCard>("AlexaCard");
                }
                else if (ConvertBotBuilderCardsToAlexaCards)
                {
                    CreateAlexaCardFromAttachment(activity, response, context);
                }
            }
        }

        private static void AddDirectivesToResponse(ITurnContext context, AlexaResponseBody response)
        {
            response.Response.Directives = context.AlexaResponseDirectives().Select(a => a).ToArray();
        }

        private static void CreateAlexaCardFromAttachment(Activity activity, AlexaResponseBody response, ITurnContext context)
        {
            #region ECA_Code
            // Carousel parse
            Attachment attachment = new Attachment();
            if (!string.IsNullOrEmpty(activity.AttachmentLayout) && activity.AttachmentLayout.Equals("carousel"))
            {
                if (activity.Attachments.Count() > 1)
                {
                    List<HeroCard> resHC = new List<HeroCard>();
                    foreach (var attachment1 in activity.Attachments)
                    {
                        var resContent = attachment1.Content.ToString().Replace("\r\n", string.Empty);
                        HeroCard heroCard = new HeroCard();
                        try
                        {
                            heroCard = JsonConvert.DeserializeObject<HeroCard>(resContent);
                        }
                        catch (JsonException ex)
                        {
                            heroCard = (HeroCard)attachment1.Content;
                        }

                        if (heroCard != null)
                        {
                            resHC.Add(heroCard);
                        }
                    }

                    attachment.ContentType = "corousel";
                    attachment.Content = resHC;
                }
                else
                {
                    attachment.Content = activity.Attachments.First().Content;
                    attachment.ContentType = HeroCard.ContentType;
                }
            }
            else
            {
                attachment = activity.Attachments != null && activity.Attachments.Any()
                   ? activity.Attachments[0]
                   : null;
            }

            if (attachment != null)
            {
                if (!string.IsNullOrEmpty(activity.Text))
                {
                    SetResponseText(ref response, activity.Text);
                    SetResponseSSML(ref response, activity.Text);
                }

                switch (attachment.ContentType)
                {
                    case HeroCard.ContentType:
                    case ThumbnailCard.ContentType:
                        if (attachment.Content is HeroCard hc)
                        {
                            SetResponseText(ref response, hc.Text);
                            SetResponseSSML(ref response, hc.Text);
                            //response.Response.Card = CreateAlexaCardFromHeroCard(attachment);
                            context.AlexaResponseDirectives().AddRange(CreateAlexaCardFromHeroCard(attachment));
                        }

                        break;
                    case "corousel":
                        // Create and Add a new directive with Corusel
                        context.AlexaResponseDirectives().AddRange(CreateAlexaDirectiveFromCarousel(attachment));
                        AddDirectivesToResponse(context, response);
                        break;
                    case SigninCard.ContentType:
                        response.Response.Card = new AlexaCard()
                        {
                            Type = AlexaCardType.LinkAccount
                        };
                        break;
                }
            }
            #endregion
        }

        private static List<DisplayDirective> CreateAlexaCardFromHeroCard(Attachment attachment)
        {
            // Change the automatic parse to card to Directive card with Image and without Image.
            var directive = new DisplayDirective();

            if (!(attachment.Content is HeroCard heroCardContent))
            {
                return null;
            }

            AlexaCard alexaCard;
            if (heroCardContent.Images != null && heroCardContent.Images.Any())
            {
                var bodyTemplate1 = new DisplayRenderBodyTemplate2()
                {
                    Title = heroCardContent.Title,
                    TextContent = new TextContent
                    {
                        PrimaryText = string.IsNullOrEmpty(heroCardContent.Title) ? null
                        : new InnerTextContent
                        {
                            Text = $"<font size=\"7\"><b>{heroCardContent.Title}</b></font>",
                            Type = TextContentType.RichText
                        },
                        SecondaryText = string.IsNullOrEmpty(heroCardContent.Subtitle) ? null
                        : new InnerTextContent
                        {
                            Text = $"<font size=\"3\">{heroCardContent.Subtitle}</font>",
                            Type = TextContentType.RichText
                        },
                        TertiaryText = string.IsNullOrEmpty(heroCardContent.Text) ? null
                        : new InnerTextContent
                        {
                            Text = $"<font size=\"2\"><br/><br/>{heroCardContent.Text}</font>",
                            Type = TextContentType.RichText
                        },
                    },
                    Image = new Image
                    {
                        Sources = new List<ImageSource> {new ImageSource
                        {
                            Url = heroCardContent.Images.FirstOrDefault()?.Url,
                        },
                    }.ToArray(),
                    },
                    BackgroundImage = new Image
                    {
                        Sources = new List<ImageSource>
                    {
                        new ImageSource
                        {
                            Url = DirectivesBackgroundImageByDefaultNoStatic
                        }
                    }.ToArray()
                    },
                };

                directive.Template = bodyTemplate1;
            }
            else
            {
                var bodyTemplate = new DisplayRenderBodyTemplate1()
                {
                    BackgroundImage = new Image { Sources = new List<ImageSource> { new ImageSource { Url = DirectivesBackgroundImageByDefaultNoStatic } }.ToArray() },
                    Title = heroCardContent.Title,
                    TextContent = new TextContent { PrimaryText = new InnerTextContent { Text = heroCardContent.Text, Type = TextContentType.PlainText } }
                };

                directive.Template = bodyTemplate;
            }

            return new List<DisplayDirective> { directive };
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken) => throw new NotImplementedException();

        #region ECA_Code
        private static List<DisplayDirective> CreateAlexaDirectiveFromCarousel(Attachment attachment)
        {
            var listItems = new List<ListItem>();
            foreach (var hc in (List<HeroCard>)attachment.Content)
            {
                if (!(hc.Title == "DELETE"))
                {
                    listItems.Add(new ListItem
                    {
                        Token = hc.Title,
                        Image = new Image
                        {
                            Sources = new List<ImageSource>
                               {
                                   new ImageSource
                                   {
                                       Url = hc.Images.First().Url
                                   }
                               }.ToArray()
                        },
                        TextContent = new TextContent
                        {
                            PrimaryText = new InnerTextContent
                            {
                                Text = hc.Title,
                            },
                            SecondaryText = new InnerTextContent
                            {
                                Text = hc.Subtitle
                            },
                            TertiaryText = new InnerTextContent
                            {
                                Text = hc.Text
                            }
                        },
                    });
                }
            }

            var directive = new DisplayDirective()
            {
                Template = new DisplayRenderListTemplate2()
                {
                    ListItems = listItems,
                    BackButton = BackButtonVisibility.VISIBLE,
                }
            };

            return new List<DisplayDirective> { directive };
        }

        /// <summary>
        /// Add to response.Response.OutputSpeech.Ssml
        /// </summary>
        private static void SetResponseSSML(ref AlexaResponseBody response, string text)
        {
            text = Regex.Replace(text, @"<[^>]*>", string.Empty);

            response.Response.OutputSpeech.Ssml = string.IsNullOrEmpty(response.Response.OutputSpeech.Ssml)
                ? text
                : response.Response.OutputSpeech.Ssml;

            response.Response.OutputSpeech.Ssml = response.Response.OutputSpeech.Ssml.Contains(text) || string.IsNullOrEmpty(response.Response.OutputSpeech.Ssml)
                ? response.Response.OutputSpeech.Ssml
                : response.Response.OutputSpeech.Ssml + $"<break time=\"{LongTimeBreak}s\"/>" + text;
        }

        /// <summary>
        /// Add to response.Response.Text
        /// </summary>
        private static void SetResponseText(ref AlexaResponseBody response, string text)
        {
            string separatorChar = response.Response.Directives?.Count() > 0
                ? "<br/>"
                : "\n";

            response.Response.OutputSpeech.Text = string.IsNullOrEmpty(response.Response.OutputSpeech.Text)
                ? text
                : response.Response.OutputSpeech.Text;

            response.Response.OutputSpeech.Text = response.Response.OutputSpeech.Text.Contains(text)
                ? response.Response.OutputSpeech.Text
                : response.Response.OutputSpeech.Text + separatorChar + text;
        }
        #endregion
    }
}
