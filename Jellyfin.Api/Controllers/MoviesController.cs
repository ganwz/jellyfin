﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// Movies controller.
    /// </summary>
    [Authorize(Policy = Policies.DefaultAuthorization)]
    public class MoviesController : BaseJellyfinApiController
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;
        private readonly IServerConfigurationManager _serverConfigurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="MoviesController"/> class.
        /// </summary>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
        /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        public MoviesController(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IDtoService dtoService,
            IServerConfigurationManager serverConfigurationManager)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
            _serverConfigurationManager = serverConfigurationManager;
        }

        /// <summary>
        /// Gets movie recommendations.
        /// </summary>
        /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
        /// <param name="parentId">Specify this to localize the search to a specific item or folder. Omit to use the root.</param>
        /// <param name="fields">Optional. The fields to return.</param>
        /// <param name="categoryLimit">The max number of categories to return.</param>
        /// <param name="itemLimit">The max number of items to return per category.</param>
        /// <response code="200">Movie recommendations returned.</response>
        /// <returns>The list of movie recommendations.</returns>
        [HttpGet("Recommendations")]
        public ActionResult<IEnumerable<RecommendationDto>> GetMovieRecommendations(
            [FromQuery] Guid? userId,
            [FromQuery] string? parentId,
            [FromQuery] ItemFields[] fields,
            [FromQuery] int categoryLimit = 5,
            [FromQuery] int itemLimit = 8)
        {
            var user = userId.HasValue && !userId.Equals(Guid.Empty)
                ? _userManager.GetUserById(userId.Value)
                : null;
            var dtoOptions = new DtoOptions()
                .AddItemFields(fields)
                .AddClientFields(Request);

            var categories = new List<RecommendationDto>();

            var parentIdGuid = string.IsNullOrWhiteSpace(parentId) ? Guid.Empty : new Guid(parentId);

            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[]
                {
                    nameof(Movie),
                    // typeof(Trailer).Name,
                    // typeof(LiveTvProgram).Name
                },
                // IsMovie = true
                OrderBy = new[] { ItemSortBy.DatePlayed, ItemSortBy.Random }.Select(i => new ValueTuple<string, SortOrder>(i, SortOrder.Descending)).ToArray(),
                Limit = 7,
                ParentId = parentIdGuid,
                Recursive = true,
                IsPlayed = true,
                DtoOptions = dtoOptions
            };

            var recentlyPlayedMovies = _libraryManager.GetItemList(query);

            var itemTypes = new List<string> { nameof(Movie) };
            if (_serverConfigurationManager.Configuration.EnableExternalContentInSuggestions)
            {
                itemTypes.Add(nameof(Trailer));
                itemTypes.Add(nameof(LiveTvProgram));
            }

            var likedMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = itemTypes.ToArray(),
                IsMovie = true,
                OrderBy = new[] { ItemSortBy.Random }.Select(i => new ValueTuple<string, SortOrder>(i, SortOrder.Descending)).ToArray(),
                Limit = 10,
                IsFavoriteOrLiked = true,
                ExcludeItemIds = recentlyPlayedMovies.Select(i => i.Id).ToArray(),
                EnableGroupByMetadataKey = true,
                ParentId = parentIdGuid,
                Recursive = true,
                DtoOptions = dtoOptions
            });

            var mostRecentMovies = recentlyPlayedMovies.Take(6).ToList();
            // Get recently played directors
            var recentDirectors = GetDirectors(mostRecentMovies)
                .ToList();

            // Get recently played actors
            var recentActors = GetActors(mostRecentMovies)
                .ToList();

            var similarToRecentlyPlayed = GetSimilarTo(user, recentlyPlayedMovies, itemLimit, dtoOptions, RecommendationType.SimilarToRecentlyPlayed).GetEnumerator();
            var similarToLiked = GetSimilarTo(user, likedMovies, itemLimit, dtoOptions, RecommendationType.SimilarToLikedItem).GetEnumerator();

            var hasDirectorFromRecentlyPlayed = GetWithDirector(user, recentDirectors, itemLimit, dtoOptions, RecommendationType.HasDirectorFromRecentlyPlayed).GetEnumerator();
            var hasActorFromRecentlyPlayed = GetWithActor(user, recentActors, itemLimit, dtoOptions, RecommendationType.HasActorFromRecentlyPlayed).GetEnumerator();

            var categoryTypes = new List<IEnumerator<RecommendationDto>>
            {
                // Give this extra weight
                similarToRecentlyPlayed,
                similarToRecentlyPlayed,

                // Give this extra weight
                similarToLiked,
                similarToLiked,
                hasDirectorFromRecentlyPlayed,
                hasActorFromRecentlyPlayed
            };

            while (categories.Count < categoryLimit)
            {
                var allEmpty = true;

                foreach (var category in categoryTypes)
                {
                    if (category.MoveNext())
                    {
                        categories.Add(category.Current);
                        allEmpty = false;

                        if (categories.Count >= categoryLimit)
                        {
                            break;
                        }
                    }
                }

                if (allEmpty)
                {
                    break;
                }
            }

            return Ok(categories.OrderBy(i => i.RecommendationType));
        }

        private IEnumerable<RecommendationDto> GetWithDirector(
            User? user,
            IEnumerable<string> names,
            int itemLimit,
            DtoOptions dtoOptions,
            RecommendationType type)
        {
            var itemTypes = new List<string> { nameof(Movie) };
            if (_serverConfigurationManager.Configuration.EnableExternalContentInSuggestions)
            {
                itemTypes.Add(nameof(Trailer));
                itemTypes.Add(nameof(LiveTvProgram));
            }

            foreach (var name in names)
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        Person = name,
                        // Account for duplicates by imdb id, since the database doesn't support this yet
                        Limit = itemLimit + 2,
                        PersonTypes = new[] { PersonType.Director },
                        IncludeItemTypes = itemTypes.ToArray(),
                        IsMovie = true,
                        EnableGroupByMetadataKey = true,
                        DtoOptions = dtoOptions
                    }).GroupBy(i => i.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb) ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
                    .Select(x => x.First())
                    .Take(itemLimit)
                    .ToList();

                if (items.Count > 0)
                {
                    var returnItems = _dtoService.GetBaseItemDtos(items, dtoOptions, user);

                    yield return new RecommendationDto
                    {
                        BaselineItemName = name,
                        CategoryId = name.GetMD5(),
                        RecommendationType = type,
                        Items = returnItems
                    };
                }
            }
        }

        private IEnumerable<RecommendationDto> GetWithActor(User? user, IEnumerable<string> names, int itemLimit, DtoOptions dtoOptions, RecommendationType type)
        {
            var itemTypes = new List<string> { nameof(Movie) };
            if (_serverConfigurationManager.Configuration.EnableExternalContentInSuggestions)
            {
                itemTypes.Add(nameof(Trailer));
                itemTypes.Add(nameof(LiveTvProgram));
            }

            foreach (var name in names)
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        Person = name,
                        // Account for duplicates by imdb id, since the database doesn't support this yet
                        Limit = itemLimit + 2,
                        IncludeItemTypes = itemTypes.ToArray(),
                        IsMovie = true,
                        EnableGroupByMetadataKey = true,
                        DtoOptions = dtoOptions
                    }).GroupBy(i => i.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb) ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
                    .Select(x => x.First())
                    .Take(itemLimit)
                    .ToList();

                if (items.Count > 0)
                {
                    var returnItems = _dtoService.GetBaseItemDtos(items, dtoOptions, user);

                    yield return new RecommendationDto
                    {
                        BaselineItemName = name,
                        CategoryId = name.GetMD5(),
                        RecommendationType = type,
                        Items = returnItems
                    };
                }
            }
        }

        private IEnumerable<RecommendationDto> GetSimilarTo(User? user, IEnumerable<BaseItem> baselineItems, int itemLimit, DtoOptions dtoOptions, RecommendationType type)
        {
            var itemTypes = new List<string> { nameof(Movie) };
            if (_serverConfigurationManager.Configuration.EnableExternalContentInSuggestions)
            {
                itemTypes.Add(nameof(Trailer));
                itemTypes.Add(nameof(LiveTvProgram));
            }

            foreach (var item in baselineItems)
            {
                var similar = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    Limit = itemLimit,
                    IncludeItemTypes = itemTypes.ToArray(),
                    IsMovie = true,
                    SimilarTo = item,
                    EnableGroupByMetadataKey = true,
                    DtoOptions = dtoOptions
                });

                if (similar.Count > 0)
                {
                    var returnItems = _dtoService.GetBaseItemDtos(similar, dtoOptions, user);

                    yield return new RecommendationDto
                    {
                        BaselineItemName = item.Name,
                        CategoryId = item.Id,
                        RecommendationType = type,
                        Items = returnItems
                    };
                }
            }
        }

        private IEnumerable<string> GetActors(IEnumerable<BaseItem> items)
        {
            var people = _libraryManager.GetPeople(new InternalPeopleQuery
            {
                ExcludePersonTypes = new[] { PersonType.Director },
                MaxListOrder = 3
            });

            var itemIds = items.Select(i => i.Id).ToList();

            return people
                .Where(i => itemIds.Contains(i.ItemId))
                .Select(i => i.Name)
                .DistinctNames();
        }

        private IEnumerable<string> GetDirectors(IEnumerable<BaseItem> items)
        {
            var people = _libraryManager.GetPeople(new InternalPeopleQuery
            {
                PersonTypes = new[] { PersonType.Director }
            });

            var itemIds = items.Select(i => i.Id).ToList();

            return people
                .Where(i => itemIds.Contains(i.ItemId))
                .Select(i => i.Name)
                .DistinctNames();
        }
    }
}
