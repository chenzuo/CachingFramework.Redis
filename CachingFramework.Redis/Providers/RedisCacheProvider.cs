﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using CachingFramework.Redis.Contracts;
using StackExchange.Redis;

namespace CachingFramework.Redis.Providers
{
    /// <summary>
    /// Cache provider implementation using Redis.
    /// </summary>
    internal class RedisCacheProvider : RedisProviderBase, ICacheProvider
    {
        #region Constructor
        /// <summary>
        /// Initializes a new instance of the <see cref="RedisCacheProvider"/> class.
        /// </summary>
        /// <param name="context">The context.</param>
        public RedisCacheProvider(RedisProviderContext context)
            :base(context)
        {
        }
        #endregion

        #region Fields
        /// <summary>
        /// The tag format for the keys representing tags
        /// </summary>
        protected const string TagFormat = ":$_tag_$:{0}";
        #endregion

        #region ICacheProvider Implementation
        /// <summary>
        /// Set the value of a key
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="ttl">The expiration.</param>
        public void SetObject<T>(string key, T value, TimeSpan? ttl = null)
        {
            var serialized = Serializer.Serialize(value);
            RedisConnection.GetDatabase().StringSet(key, serialized, ttl);
        }
        /// <summary>
        /// Set the value of a key, associating the key with the given tag(s).
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="tags">The tags.</param>
        /// <param name="ttl">The expiry.</param>
        public void SetObject<T>(string key, T value, string[] tags, TimeSpan? ttl = null)
        {
            var serialized = Serializer.Serialize(value);
            var db = RedisConnection.GetDatabase();
            var batch = db.CreateBatch();
            foreach (var tagName in tags)
            {
                var tag = FormatTag(tagName);
                var expiration = GetExpiration(db, tag, ttl);
                // Add the tag-key relation
                batch.SetAddAsync(tag, key);
                // Set the expiration
                if (expiration != null)
                {
                    if (expiration == TimeSpan.MaxValue)
                    {
                        batch.KeyPersistAsync(tag);
                    }
                    else
                    {
                        batch.KeyExpireAsync(tag, expiration);
                    }
                }
            }
            // Add the key-value
            batch.StringSetAsync(key, serialized, ttl);
            batch.Execute();
        }
        /// <summary>
        /// Relates the given tags to a key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="tags">The tag(s).</param>
        public void AddTagsToKey(string key, string[] tags)
        {
            var db = RedisConnection.GetDatabase();
            var batch = db.CreateBatch();
            foreach (var tag in tags)
            {
                batch.SetAddAsync(FormatTag(tag), key);
            }
            batch.Execute();
        }
        /// <summary>
        /// Removes the relation between the given tags and a key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="tags">The tag(s).</param>
        public void RemoveTagsFromKey(string key, string[] tags)
        {
            var db = RedisConnection.GetDatabase();
            var batch = db.CreateBatch();
            foreach (var tagName in tags)
            {
                var tag = FormatTag(tagName);
                batch.SetRemoveAsync(tag, key);
            }
            batch.Execute();
        }
        /// <summary>
        /// Removes all the keys related to the given tag(s).
        /// </summary>
        /// <param name="tags">The tags.</param>
        public void InvalidateKeysByTag(string[] tags)
        {
            var db = RedisConnection.GetDatabase();
            var keys = GetKeysByAllTagsNoCleanup(db, tags);
            var batch = db.CreateBatch();
            // Delete the keys
            foreach (var key in keys)
            {
                batch.KeyDeleteAsync(key);
            }
            // Delete the tags
            foreach (var tagName in tags)
            {
                batch.KeyDeleteAsync(FormatTag(tagName));
            }
            batch.Execute();
        }
        /// <summary>
        /// Gets all the keys related to the given tag(s).
        /// Returns a hashset with the keys.
        /// Also does the cleanup for the given tags if the parameter cleanUp is true.
        /// Since it is cluster compatible, and cluster does not allow multi-key operations, we cannot use SUNION or LUA scripts.
        /// </summary>
        /// <param name="tags">The tags.</param>
        /// <param name="cleanUp">True to return only the existing keys within the tags (slower). Default is false.</param>
        /// <returns>HashSet{System.String}.</returns>
        public ISet<string> GetKeysByTag(string[] tags, bool cleanUp = false)
        {
            var db = RedisConnection.GetDatabase();
            if (cleanUp)
            {
                return GetKeysByAllTagsWithCleanup(db, tags);
            }
            return GetKeysByAllTagsNoCleanup(db, tags);
        }
        /// <summary>
        /// Returns all the objects that has the given tag(s) related.
        /// Assumes all the objects are of the same type <typeparamref name="T" />.
        /// </summary>
        /// <typeparam name="T">The objects types</typeparam>
        /// <param name="tags">The tags</param>
        /// <returns>IEnumerable{``0}.</returns>
        public IEnumerable<T> GetObjectsByTag<T>(string[] tags)
        {
            var db = RedisConnection.GetDatabase();
            ISet<string> keys = GetKeysByAllTagsNoCleanup(db, tags);
            foreach (var key in keys)
            {
                var value = db.StringGet(key);
                if (value.HasValue)
                {
                    yield return Serializer.Deserialize<T>(value);    
                }
            }
        }
        /// <summary>
        /// Gets a deserialized value from a key
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <returns>``0.</returns>
        public T GetObject<T>(string key)
        {
            var cacheValue = RedisConnection.GetDatabase().StringGet(key);
            if (cacheValue.HasValue)
            {
                return Serializer.Deserialize<T>(cacheValue);
            }
            return default(T);
        }
        /// <summary>
        /// Returns the entire collection of tags
        /// </summary>
        public ISet<string> GetAllTags()
        {
            var tags = new List<RedisKey>();
            RunInAllMasters(svr => tags.AddRange(svr.Keys(0, string.Format(TagFormat, "*"))));
            int startIndex = string.Format(TagFormat, "").Length;
            return new HashSet<string>(tags.Select(rv => rv.ToString().Substring(startIndex)));
        }
        /// <summary>
        /// Removes the specified key-value.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        /// <remarks>Redis command: DEL key</remarks>
        public bool Remove(string key)
        {
            return RedisConnection.GetDatabase().KeyDelete(key);
        }
        /// <summary>
        /// Sets the specified value to a hashset using the pair hashKey+field.
        /// (The latest expiration applies to the whole key)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="field">The field key</param>
        /// <param name="value">The value to store</param>
        /// <param name="ttl">Set the current expiration timespan to the whole key (not only this hash). NULL to keep the current expiration.</param>
        public void SetHashed<T>(string key, string field, T value, TimeSpan? ttl = null)
        {
            var db = RedisConnection.GetDatabase();
            var batch = db.CreateBatch();
            batch.HashSetAsync(key, field, Serializer.Serialize(value));
            var expiration = GetExpiration(db, key, ttl);
            if (expiration != null)
            {
                if (expiration == TimeSpan.MaxValue)
                {
                    batch.KeyPersistAsync(key);
                }
                else
                {
                    batch.KeyExpireAsync(key, expiration);
                }
            }
            batch.Execute();
        }
        /// <summary>
        /// Sets the specified key/values pairs to a hashset.
        /// (The latest expiration applies to the whole key)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="fieldValues">The field keys and values to store</param>
        /// <param name="ttl">Set the current expiration timespan to the whole key (not only this hash). NULL to keep the current expiration.</param>
        public void SetHashed<T>(string key, IDictionary<string, T> fieldValues, TimeSpan? ttl = null)
        {
            var db = RedisConnection.GetDatabase();
            db.HashSet(key, fieldValues.Select(x => new HashEntry(x.Key, Serializer.Serialize(x.Value))).ToArray());
        }
        /// <summary>
        /// Gets a specified hased value from a key
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="field">The field.</param>
        public T GetHashed<T>(string key, string field)
        {
            var redisValue = RedisConnection.GetDatabase().HashGet(key, field);
            return !redisValue.IsNull ? Serializer.Deserialize<T>(redisValue) : default(T);
        }
        /// <summary>
        /// Removes a specified hased value from cache
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="field">The field.</param>
        public bool RemoveHashed(string key, string field)
        {
            return RedisConnection.GetDatabase().HashDelete(key, field);
        }
        /// <summary>
        /// Gets all the values from a hash, assuming all the values in the hash are of the same type <typeparamref name="T" />.
        /// The keys of the dictionary are the field names and the values are the objects
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        public IDictionary<string, T> GetHashedAll<T>(string key)
        {
            return RedisConnection.GetDatabase()
                .HashGetAll(key)
                .ToDictionary(k => k.Name.ToString(), v => Serializer.Deserialize<T>(v.Value));
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Returns the maximum TTL between the current key TTL and the given TTL
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="key">The key.</param>
        /// <param name="ttl">The TTL.</param>
        private static TimeSpan? GetExpiration(IDatabase db, string key, TimeSpan? ttl)
        {
            bool preexistent = db.KeyExists(key);
            TimeSpan? curr = preexistent ? db.KeyTimeToLive(key) : null;
            if (ttl != null && curr != null)
            {
                // We have an expiration on both keys, use the max for the key
                return curr > ttl ? curr : ttl;
            }
            if (preexistent && ttl == null)
            {
                //Key is preexistent and no expiration given
                return TimeSpan.MaxValue;
            }
            return ttl;
        }
        /// <summary>
        /// Get all the keys related to a tag(s), the keys returned are not tested for existence.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="tags">The tags.</param>
        private static ISet<string> GetKeysByAllTagsNoCleanup(IDatabase db, params string[] tags)
        {
            var keys = new List<string>();
            foreach (var tagName in tags)
            {
                var tag = FormatTag(tagName);
                if (db.KeyType(tag) == RedisType.Set)
                {
                    keys.AddRange(db.SetMembers(tag).Select(rv => rv.ToString()));
                }
            }
            return new HashSet<string>(keys);
        }
        /// <summary>
        /// Get all the keys related to a tag(s), only returns the keys that currently exists.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="tags">The tags.</param>
        private static ISet<string> GetKeysByAllTagsWithCleanup(IDatabase db, params string[] tags)
        {
            var ret = new HashSet<string>();
            var toRemove = new List<RedisValue>();
            foreach (var tagName in tags)
            {
                var tag = FormatTag(tagName);
                if (db.KeyType(tag) == RedisType.Set)
                {
                    var tagKeys = db.SetMembers(tag);
                    //Get the existing keys and delete the dead keys
                    foreach (var key in tagKeys)
                    {
                        if (db.KeyExists(key.ToString()))
                        {
                            ret.Add(key);
                        }
                        else
                        {
                            toRemove.Add(key);
                        }
                    }
                    if (toRemove.Count > 0)
                    {
                        db.SetRemove(tag, toRemove.ToArray());
                    }
                }
            }
            return ret;
        }
        /// <summary>
        /// Return the RedisKey used for a tag
        /// </summary>
        /// <param name="tag">The tag name</param>
        /// <returns>RedisKey.</returns>
        private static RedisKey FormatTag(string tag)
        {
            return string.Format(TagFormat, tag);
        }
        /// <summary>
        /// Runs a Server command in all the master servers.
        /// </summary>
        /// <param name="action">The action.</param>
        private void RunInAllMasters(Action<IServer> action)
        {
            ICollection<ClusterNode> nodes = null;
            foreach (var ep in RedisConnection.GetEndPoints())
            {
                if (RedisConnection.GetServer(ep).IsConnected)
                {
                    nodes = RedisConnection.GetServer(ep).ClusterConfiguration.Nodes;
                    break;
                }
            }
            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    if (!node.IsSlave)
                    {
                        action(RedisConnection.GetServer(node.EndPoint));
                    }
                }
            }
        }
        /// <summary>
        /// Flushes all the databases on every master node.
        /// </summary>
        public void FlushAll()
        {
            RunInAllMasters(svr => svr.FlushAllDatabases());
        }
        #endregion
    }
}