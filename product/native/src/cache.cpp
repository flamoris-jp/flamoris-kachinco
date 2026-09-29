#include "kachinco_native.h"
#include <cstring>
#include <list>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
struct kn_buffer { std::shared_ptr<const std::vector<uint8_t>> data; };
struct cache_entry { std::string key; std::shared_ptr<const std::vector<uint8_t>> data; int64_t charge; };
struct kn_cache {
    std::mutex gate;
    int64_t limit;
    int32_t entry_limit;
    kn_cache_statistics statistics{};
    std::list<cache_entry> lru;
    std::unordered_map<std::string, std::list<cache_entry>::iterator> entries;
    kn_cache(int64_t bytes, int32_t count) : limit(bytes), entry_limit(count) { }
};
int32_t KN_CALL kn_cache_create(int64_t byte_limit, int32_t entry_limit, kn_cache** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = nullptr;
    if (byte_limit < 0 || entry_limit < 0) return KN_INVALID_ARGUMENT;
    try { *output = new kn_cache(byte_limit, entry_limit); return KN_OK; }
    catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
    catch (...) { return KN_INTERNAL_ERROR; }
}
void KN_CALL kn_cache_destroy(kn_cache* cache) noexcept { delete cache; }
int32_t KN_CALL kn_cache_put(kn_cache* cache, const char* key, const uint8_t* data, uint32_t size, int64_t charge) noexcept {
    if (!cache || !key || std::strlen(key) > 1024 || (!data && size) || charge < 0 ||
        static_cast<uint64_t>(size) > static_cast<uint64_t>(charge) + 32 || size > 256 * 1024 * 1024) return KN_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(cache->gate);
        auto old = cache->entries.find(key);
        if (old != cache->entries.end()) {
            cache->statistics.bytes -= old->second->charge; cache->lru.erase(old->second); cache->entries.erase(old);
        }
        if (charge > cache->limit || cache->entry_limit == 0) return KN_OK;
        auto bytes = std::make_shared<std::vector<uint8_t>>();
        if (size) bytes->assign(data, data + size);
        while (cache->entries.size() >= static_cast<size_t>(cache->entry_limit) || cache->statistics.bytes > cache->limit - charge) {
            auto& last = cache->lru.back(); cache->statistics.bytes -= last.charge;
            cache->entries.erase(last.key); cache->lru.pop_back(); ++cache->statistics.evictions;
        }
        cache->lru.push_front({key, std::move(bytes), charge});
        try { cache->entries.emplace(cache->lru.front().key, cache->lru.begin()); }
        catch (...) { cache->lru.pop_front(); throw; }
        cache->statistics.bytes += charge;
        return KN_OK;
    } catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
      catch (...) { return KN_INTERNAL_ERROR; }
}
int32_t KN_CALL kn_cache_get(kn_cache* cache, const char* key, kn_buffer** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = nullptr;
    if (!cache || !key || std::strlen(key) > 1024) return KN_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(cache->gate);
        auto found = cache->entries.find(key);
        if (found == cache->entries.end()) { ++cache->statistics.misses; return KN_OK; }
        auto buffer = std::make_unique<kn_buffer>(); buffer->data = found->second->data;
        cache->lru.splice(cache->lru.begin(), cache->lru, found->second); ++cache->statistics.hits;
        *output = buffer.release(); return KN_OK;
    } catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
      catch (...) { return KN_INTERNAL_ERROR; }
}
int32_t KN_CALL kn_cache_clear(kn_cache* cache) noexcept {
    if (!cache) return KN_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(cache->gate);
        cache->entries.clear(); cache->lru.clear(); cache->statistics.bytes = 0; return KN_OK;
    } catch (...) { return KN_INTERNAL_ERROR; }
}
int32_t KN_CALL kn_cache_stats(kn_cache* cache, kn_cache_statistics* output, uint32_t size) noexcept {
    if (!output || size != sizeof(*output)) return KN_INVALID_ARGUMENT;
    *output = {};
    if (!cache) return KN_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(cache->gate);
        *output = cache->statistics; output->entries = static_cast<int64_t>(cache->entries.size()); return KN_OK;
    } catch (...) { return KN_INTERNAL_ERROR; }
}
void KN_CALL kn_buffer_destroy(kn_buffer* buffer) noexcept { delete buffer; }
int32_t KN_CALL kn_buffer_size(const kn_buffer* buffer, uint32_t* size) noexcept {
    if (!size) return KN_INVALID_ARGUMENT;
    *size = 0;
    if (!buffer) return KN_INVALID_ARGUMENT;
    *size = static_cast<uint32_t>(buffer->data->size()); return KN_OK;
}
int32_t KN_CALL kn_buffer_copy(const kn_buffer* buffer, uint8_t* output, uint32_t capacity) noexcept {
    if (!buffer || capacity < buffer->data->size() || (!output && !buffer->data->empty())) return KN_INVALID_ARGUMENT;
    if (!buffer->data->empty()) std::memcpy(output, buffer->data->data(), buffer->data->size());
    return KN_OK;
}
