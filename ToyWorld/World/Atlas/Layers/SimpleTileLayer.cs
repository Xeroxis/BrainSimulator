﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Utils.VRageRIP.Lib.Extensions;
using VRageMath;
using World.GameActors;
using World.GameActors.Tiles;
using World.GameActors.Tiles.Obstacle;
using World.Physics;

namespace World.Atlas.Layers
{
    public class SimpleTileLayer : ITileLayer
    {
        private const int TILESETS_BITS = 12;
        private const int TILESETS_OFFSET = 1 << TILESETS_BITS; // Must be larger than the number of tiles in any tileset and must correspond to the BasicOffset.vert shader
        private const int BACKGROUND_TILE_NUMBER = 6;
        private const int OBSTACLE_TILE_NUMBER = 7;


        #region Summer/winter stuff

        private readonly Random m_random;

        private float m_summer; // Local copy of the Atlas' summer
        private Vector3 m_summerCache;
        private bool IsWinter { get { return m_summer < 0.25f; } }

        private int m_tileCount;

        #endregion


        public int Width { get; set; }
        public int Height { get; set; }

        public Tile[][] Tiles { get; set; }
        public bool Render { get; set; }
        public LayerType LayerType { get; set; }


        public SimpleTileLayer(LayerType layerType, int width, int height, Random random = null)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException("width", "Tile width has to be positive");
            if (height <= 0)
                throw new ArgumentOutOfRangeException("height", "Tile height has to be positive");
            Contract.EndContractBlock();

            m_random = random ?? new Random();

            LayerType = layerType;
            m_summerCache.Z = m_random.Next();

            Height = height;
            Width = width;

            Tiles = ArrayCreator.CreateJaggedArray<Tile[][]>(width, height);

            Render = true;
        }


        public void UpdateTileStates(Atlas atlas)
        {
            m_summer = atlas.Summer;
        }


        public Tile GetActorAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return new Obstacle(0);
            }
            return Tiles[x][y];
        }

        public Tile GetActorAt(Vector2I coordinates)
        {
            return GetActorAt(coordinates.X, coordinates.Y);
        }

        public void GetTileTypesAt(Vector2I topLeft, Vector2I size, int[] tileTypes)
        {
            Vector2I intBotRight = topLeft + size;
            Rectangle rectangle = new Rectangle(topLeft, intBotRight - topLeft);
            GetTileTypesAt(rectangle, tileTypes);
        }

        public void GetTileTypesAt(Rectangle rectangle, int[] tileTypes)
        {
            // Store the resulting types in the parameter
            Debug.Assert(rectangle.Size.Size() <= tileTypes.Length, "Too little space for the grid tile types!");

            // Use cached getter value
            int viewRight = rectangle.Right;

            int left = Math.Max(rectangle.Left, Math.Min(0, rectangle.Left + rectangle.Width));
            int right = Math.Min(viewRight, Math.Max(Tiles.Length, viewRight - rectangle.Width));
            // Rectangle origin is in top-left; it's top is thus our bottom
            int bot = Math.Max(rectangle.Top, Math.Min(0, rectangle.Top + rectangle.Height));
            int top = Math.Min(rectangle.Bottom, Math.Max(Tiles[0].Length, rectangle.Bottom - rectangle.Height));


            // TODO : Move to properties
            int idx = 0;
            int defaultTileOffset = 0;
            if (LayerType == LayerType.Background)
            {
                defaultTileOffset = BACKGROUND_TILE_NUMBER;
            }
            else if (LayerType == LayerType.Obstacle)
            {
                defaultTileOffset = OBSTACLE_TILE_NUMBER;
            }

            // Rows before start of map
            for (int j = rectangle.Top; j < bot; j++)
            {
                for (int i = rectangle.Left; i < viewRight; i++)
                    tileTypes[idx++] = GetDefaultTileOffset(i, j, defaultTileOffset);
            }

            // Rows inside of map
            for (var j = bot; j < top; j++)
            {
                // Tiles before start of map
                for (int i = rectangle.Left; i < left; i++)
                    tileTypes[idx++] = GetDefaultTileOffset(i, j, defaultTileOffset);

                // Tiles inside of map
                for (var i = left; i < right; i++)
                {
                    var tile = Tiles[i][j];
                    if (tile != null)
                        tileTypes[idx++] = GetDefaultTileOffset(i, j, tile.TilesetId);
                    else
                        tileTypes[idx++] = 0; // inside map: must be always 0
                }

                // Tiles after end of map
                for (int i = right; i < viewRight; i++)
                    tileTypes[idx++] = GetDefaultTileOffset(i, j, defaultTileOffset);
            }

            // Rows after end of map
            for (int j = top; j < rectangle.Bottom; j++)
            {
                for (int i = rectangle.Left; i < viewRight; i++)
                    tileTypes[idx++] = GetDefaultTileOffset(i, j, defaultTileOffset);
            }
        }

        // This is rather slow to compute, but we assume only small portions of grid view will be in sight
        private int GetDefaultTileOffset(int x, int y, int defaultTileOffset)
        {
            if (!IsWinter)
                return defaultTileOffset;

            m_summerCache.X = x;
            m_summerCache.Y = y;
            float hash = (float)((Math.Abs(m_summerCache.GetHash()) % (double)int.MaxValue) * (1 / (double)int.MaxValue)); // Should be uniformly distributed between 0, 1

            float weatherChangeIntensityFactor = 1 - m_summer * 4; // It is Oct to Mar, use stronger intensity towards Dec/Jan
            weatherChangeIntensityFactor = GetModifiedWinterIntensityFactor(weatherChangeIntensityFactor);

            const float maxWinterIntensityFactor = 0.8f;
            const float winterOffset = 0.02f;
            weatherChangeIntensityFactor *= maxWinterIntensityFactor + winterOffset; // Makes even the hardest winter (summer close to 0) not fill everything with snow
            weatherChangeIntensityFactor -= winterOffset; // Makes winter end a little sooner

            if (hash < weatherChangeIntensityFactor)
                return defaultTileOffset + TILESETS_OFFSET;

            return defaultTileOffset;
        }

        private float GetModifiedWinterIntensityFactor(float winterChangeIntensity)
        {
            return (float)Math.Sin(winterChangeIntensity * MathHelper.PiOver2); // Rises higher than y=x for x in (0,1)
        }


        public bool ReplaceWith<T>(GameActorPosition original, T replacement)
        {
            int x = (int)Math.Floor(original.Position.X);
            int y = (int)Math.Floor(original.Position.Y);
            Tile item = GetActorAt(x, y);

            if (item != original.Actor) return false;

            Tiles[x][y] = null;
            Tile tileReplacement = replacement as Tile;

            if (replacement == null)
            {
                m_tileCount--;
                return true;
            }

            if (Tiles[x][y] == null)
                m_tileCount++;

            Tiles[x][y] = tileReplacement;
            return true;
        }

        public bool Add(GameActorPosition gameActorPosition)
        {
            int x = (int)gameActorPosition.Position.X;
            int y = (int)gameActorPosition.Position.Y;

            if (Tiles[x][y] != null)
                return false;

            Tile actor = gameActorPosition.Actor as Tile;
            Tiles[x][y] = actor;

            if (actor != null)
                m_tileCount++;

            return true;
        }

        public void AddInternal(int x, int y, Tile tile)
        {
            Tiles[x][y] = tile;
            m_tileCount++;
        }
    }
}
